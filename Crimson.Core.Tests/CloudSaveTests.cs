using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Crimson.Core;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;

namespace Crimson.Tests;

public sealed class CloudSaveTests : IDisposable
{
	private const string AppName = "SyntheticApp";
	private const string Account = "abc123";

	private readonly string _root = Path.Combine(Path.GetTempPath(), $"crimson-cloud-save-{Guid.NewGuid():N}");
	private readonly Serilog.Core.Logger _logger = new LoggerConfiguration().CreateLogger();
	private readonly HttpClient _http = new();
	private readonly FakeCloud _cloud = new();

	private string InstallPath => Path.Combine(_root, "game");
	private string SaveFolder => Path.Combine(InstallPath, "Saves");

	[Fact]
	public void PackageRoundTripsThroughTheStrictParsers()
	{
		var folder = Path.Combine(_root, "package");
		Directory.CreateDirectory(Path.Combine(folder, "sub"));
		var large = RandomNumberGenerator.GetBytes(CloudSavePackager.ChunkSize + 4321);
		File.WriteAllBytes(Path.Combine(folder, "large.sav"), large);
		File.WriteAllBytes(Path.Combine(folder, "empty.sav"), []);
		File.WriteAllText(Path.Combine(folder, "sub", "sauvegardé.sav"), "accent");

		var package = CloudSavePackager.Package(
			folder,
			["large.sav", "empty.sav", "sub/sauvegardé.sav"],
			AppName,
			Account,
			"{installdir}/Saves",
			"2026.11.09-10.00.00",
			Path.Combine(_root, "package-output"));

		var manifest = Manifest.ReadAll(File.ReadAllBytes(package.Manifest.TempFile));
		Assert.Equal(AppName + Account, manifest.ManifestMeta.AppName);
		Assert.Equal("2026.11.09-10.00.00", manifest.ManifestMeta.BuildVersion);
		Assert.Equal("{installdir}/Saves", manifest.CustomFields["CloudSaveFolder"]);
		Assert.Equal("manifests/2026.11.09-10.00.00.manifest", package.Manifest.RemotePath);
		Assert.Equal(2, package.Chunks.Count);

		var chunks = new Dictionary<string, byte[]>();
		foreach (var info in manifest.CDL.Elements)
		{
			var upload = Assert.Single(package.Chunks, file => file.RemotePath == info.Path);
			Assert.StartsWith("ChunksV4/", upload.RemotePath);
			var chunk = Chunk.ReadBuffer(File.ReadAllBytes(upload.TempFile));
			chunk.ValidateAgainst(info);
			Assert.Equal(CloudSavePackager.ChunkSize, chunk.Data.Length);
			Assert.Equal(unchecked((ulong)info.Hash), RollingHash.ComputeHash(chunk.Data));
			chunks[info.GuidStr] = chunk.Data;
		}

		var rebuilt = manifest.FileManifestList.GetFileByPath("large.sav").ChunkParts
			.SelectMany(part => chunks[part.GuidStr].Skip(part.Offset).Take(part.Size))
			.ToArray();
		Assert.Equal(large, rebuilt);
		Assert.Equal(SHA1.HashData(large), manifest.FileManifestList.GetFileByPath("large.sav").Hash);
		Assert.Equal(0, manifest.FileManifestList.GetFileByPath("empty.sav").FileSize);
		Assert.Equal(6, manifest.FileManifestList.GetFileByPath("sub/sauvegardé.sav").FileSize);
	}

	[Theory]
	[InlineData(null, null, null, null, false, CloudSaveAction.NoSaves)]
	[InlineData("L", null, null, null, false, CloudSaveAction.Upload)]
	[InlineData(null, "R", null, null, false, CloudSaveAction.Download)]
	[InlineData("L", "R", null, null, false, CloudSaveAction.Conflict)]
	[InlineData("L", "R", null, null, true, CloudSaveAction.None)]
	[InlineData("L", "R", "R", "L", false, CloudSaveAction.None)]
	[InlineData("L2", "R", "R", "L", false, CloudSaveAction.Upload)]
	[InlineData("L", "R2", "R", "L", false, CloudSaveAction.Download)]
	[InlineData("L2", "R2", "R", "L", false, CloudSaveAction.Conflict)]
	[InlineData(null, "R", "R", "L", false, CloudSaveAction.Download)]
	internal void DecideOnlyActsWhenOneSideChanged(
		string? local,
		string? remote,
		string? baselineManifest,
		string? baselineLocal,
		bool contentMatches,
		CloudSaveAction expected)
	{
		var baseline = baselineManifest is null ? null : new CloudSaveBaseline(baselineManifest, baselineLocal);
		Assert.Equal(expected, CloudSaveManager.Decide(local, remote, baseline, contentMatches));
	}

	[Theory]
	[InlineData("{installdir}/Saves")]
	[InlineData("{InstallDir}\\Saves\\{EpicID}")]
	[InlineData("{usersavedgames}/Game/slot_{epicid}")]
	public void ResolvesSupportedTemplates(string template)
	{
		var location = CloudSaveLocation.TryResolve(CreateGame(template), Account, Folders());

		Assert.NotNull(location);
		Assert.StartsWith(_root, location.Folder, StringComparison.OrdinalIgnoreCase);
	}

	[Theory]
	[InlineData("{installdir}/../../Windows")]
	[InlineData("{installdir}/./Saves")]
	[InlineData("C:/Windows")]
	[InlineData("Saves/{installdir}")]
	[InlineData("{installdir}/{unknown}")]
	[InlineData("{epicid}/Saves")]
	[InlineData("{userdir}")]
	public void RejectsUnsafeTemplates(string template)
	{
		Assert.Null(CloudSaveLocation.TryResolve(CreateGame(template), Account, Folders()));
	}

	[Fact]
	public void BareRootRequiresAnIncludeList()
	{
		var game = CreateGame("{userdir}", include: "Game/*");

		Assert.NotNull(CloudSaveLocation.TryResolve(game, Account, Folders()));
	}

	[Fact]
	public void SignedLinksAcceptOnlyHttpsStorageHosts()
	{
		Assert.NotNull(EpicEndpointPolicy.RequireCloudSaveLinkUri("https://bucket.s3.amazonaws.com/a?sig=1"));
		Assert.NotNull(EpicEndpointPolicy.RequireCloudSaveLinkUri("https://saves.epicgames.com/a?sig=1"));
		Assert.Throws<InvalidOperationException>(() => EpicEndpointPolicy.RequireCloudSaveLinkUri("http://bucket.s3.amazonaws.com/a"));
		Assert.Throws<InvalidOperationException>(() => EpicEndpointPolicy.RequireCloudSaveLinkUri("https://example.com/a"));
	}

	[Fact]
	public async Task FirstSyncUploadsAndManifestIsWrittenLast()
	{
		WriteSave("slot.sav", "first");
		var manager = CreateManager();

		var result = await manager.SyncAsync(AppName);

		Assert.Equal(CloudSaveOutcome.Uploaded, result.Outcome);
		Assert.EndsWith(".manifest", _cloud.WriteOrder[^1]);
		Assert.Equal(2, _cloud.WriteOrder.Count);
		Assert.Equal(CloudSaveOutcome.UpToDate, (await manager.SyncAsync(AppName)).Outcome);
	}

	[Fact]
	public async Task OneSidedChangesSyncAndDownloadsBackUpReplacedFiles()
	{
		WriteSave("slot.sav", "first");
		var manager = CreateManager();
		await manager.SyncAsync(AppName);

		WriteSave("slot.sav", "second");
		Assert.Equal(CloudSaveOutcome.Uploaded, (await manager.SyncAsync(AppName)).Outcome);

		// A different machine's data directory has no baseline for this game.
		WriteSave("slot.sav", "stale");
		var otherMachine = CreateManager("other-machine");
		Assert.Equal(CloudSaveOutcome.Conflict, (await otherMachine.SyncAsync(AppName)).Outcome);
		Assert.Equal("stale", ReadSave("slot.sav"));

		WriteSave("extra.sav", "not in cloud");
		Assert.Equal(CloudSaveOutcome.Downloaded, (await otherMachine.SyncAsync(AppName, CloudSaveResolution.UseCloud)).Outcome);
		Assert.Equal("second", ReadSave("slot.sav"));
		Assert.False(File.Exists(Path.Combine(SaveFolder, "extra.sav")));

		var backup = Assert.Single(Directory.GetDirectories(Path.Combine(_root, "other-machine", "backups", AppName)));
		Assert.Equal("stale", File.ReadAllText(Path.Combine(backup, "slot.sav")));
		Assert.Equal("not in cloud", File.ReadAllText(Path.Combine(backup, "extra.sav")));
		Assert.Equal(CloudSaveOutcome.UpToDate, (await otherMachine.SyncAsync(AppName)).Outcome);
	}

	[Fact]
	public async Task MatchingContentWithoutBaselineIsAlreadyInSync()
	{
		WriteSave("slot.sav", "same");
		await CreateManager().SyncAsync(AppName);
		var writes = _cloud.WriteOrder.Count;

		var result = await CreateManager("other-machine").SyncAsync(AppName);

		Assert.Equal(CloudSaveOutcome.UpToDate, result.Outcome);
		Assert.Equal(writes, _cloud.WriteOrder.Count);
	}

	[Fact]
	public async Task KeepLocalResolvesAConflictByUploading()
	{
		WriteSave("slot.sav", "cloud");
		await CreateManager().SyncAsync(AppName);
		WriteSave("slot.sav", "local");
		var other = CreateManager("other-machine");
		Assert.Equal(CloudSaveOutcome.Conflict, (await other.SyncAsync(AppName)).Outcome);

		Assert.Equal(CloudSaveOutcome.Uploaded, (await other.SyncAsync(AppName, CloudSaveResolution.KeepLocal)).Outcome);

		Assert.Equal("local", ReadSave("slot.sav"));
		Assert.Equal(CloudSaveOutcome.UpToDate, (await other.SyncAsync(AppName)).Outcome);
	}

	[Fact]
	public async Task CorruptChunkLeavesLocalSavesUntouched()
	{
		WriteSave("slot.sav", "cloud");
		await CreateManager().SyncAsync(AppName);
		var chunkKey = _cloud.Files.Keys.Single(key => key.Contains("ChunksV4", StringComparison.Ordinal));
		var bytes = _cloud.Files[chunkKey];
		bytes[^1] ^= 0xFF;
		WriteSave("slot.sav", "local");

		var result = await CreateManager("other-machine").SyncAsync(AppName, CloudSaveResolution.UseCloud);

		Assert.Equal(CloudSaveOutcome.Failed, result.Outcome);
		Assert.Equal("local", ReadSave("slot.sav"));
	}

	[Fact]
	public async Task ManifestPathTraversalIsRefusedBeforeAnyFileChanges()
	{
		WriteSave("slot.sav", "local");
		var buffer = new byte[CloudSavePackager.ChunkSize];
		buffer[0] = 1;
		var guid = CloudSavePackager.NewGuid();
		var (info, chunkBytes) = CloudSavePackager.BuildChunk(guid, buffer, 1);
		var file = new FileManifest { Filename = "../escaped.sav", Hash = SHA1.HashData(new byte[] { 1 }) };
		file.ChunkParts.Add(new ChunkPart(guid, 0, 1));
		_cloud.Files[$"savesync/{Account}/{AppName}/{info.Path}"] = chunkBytes;
		_cloud.Files[$"savesync/{Account}/{AppName}/manifests/2026.01.01-00.00.00.manifest"] =
			CloudSavePackager.BuildManifest(AppName + Account, "2026.01.01-00.00.00", "{installdir}/Saves", [info], [file]);

		var result = await CreateManager().SyncAsync(AppName, CloudSaveResolution.UseCloud);

		Assert.Equal(CloudSaveOutcome.Failed, result.Outcome);
		Assert.False(File.Exists(Path.Combine(InstallPath, "escaped.sav")));
		Assert.Equal("local", ReadSave("slot.sav"));
	}

	[Fact]
	public async Task FiltersLimitWhatIsUploadedAndWhatADownloadRemoves()
	{
		WriteSave("slot.sav", "keep");
		WriteSave("cache.tmp", "ignored");
		var manager = CreateManager(exclude: "*.tmp");
		await manager.SyncAsync(AppName);
		Assert.Single(_cloud.WriteOrder, key => key.Contains("ChunksV4", StringComparison.Ordinal));
		var manifest = Manifest.ReadAll(_cloud.Files[_cloud.WriteOrder[^1]]);
		Assert.Equal("slot.sav", Assert.Single(manifest.FileManifestList.Elements).Filename);

		await manager.SyncAsync(AppName, CloudSaveResolution.UseCloud);

		Assert.Equal("ignored", ReadSave("cache.tmp"));
	}

	[Fact]
	public async Task NewManifestSortsAfterACloudManifestFromTheFuture()
	{
		WriteSave("slot.sav", "first");
		var manager = CreateManager();
		await manager.SyncAsync(AppName);
		var key = _cloud.Files.Keys.Single(file => file.EndsWith(".manifest", StringComparison.Ordinal));
		_cloud.Files["manifests/2099.01.01-00.00.00.manifest"] = _cloud.Files[key];

		WriteSave("slot.sav", "second");
		await manager.SyncAsync(AppName, CloudSaveResolution.KeepLocal);

		Assert.EndsWith("2099.01.01-00.00.01.manifest", _cloud.WriteOrder[^1]);
	}

	[Fact]
	public async Task StaleCachedMetadataIsFetchedOnceToLearnCloudSaveFolder()
	{
		var storage = CreateStorage();
		var game = CreateGame("{installdir}/Saves");
		game.LocalAppState = null;
		game.MetadataSchema = 0;
		storage.SaveMetaData(game);
		var repository = new MetadataRepository();
		var auth = new AuthManager(_logger, storage, new TestCredentialProtector(), _http);
		var library = new LibraryManager(_logger, repository, storage, auth);

		await library.GetLibraryData(forceUpdate: false);
		library.InvalidateCache();
		await library.GetLibraryData(forceUpdate: false);

		Assert.Equal(1, repository.MetadataRequests);
		Assert.Equal("{installdir}/Saves", storage.GetGameMetaData(AppName).Metadata.CustomAttributes.CloudSaveFolder?.Value);
	}

	[Fact]
	public void CloudSaveFolderSurvivesTheMetadataCache()
	{
		var storage = CreateStorage();
		var game = CreateGame("{installdir}/Saves", include: "*.sav");
		game.MetadataSchema = Game.CurrentMetadataSchema;
		storage.SaveMetaData(game);

		var reloaded = CreateStorage().GetGameMetaData(AppName);

		Assert.Equal(Game.CurrentMetadataSchema, reloaded.MetadataSchema);
		Assert.Equal("{installdir}/Saves", reloaded.Metadata.CustomAttributes.CloudSaveFolder?.Value);
		Assert.Equal("*.sav", reloaded.Metadata.CustomAttributes.CloudIncludeList?.Value);
	}

	public void Dispose()
	{
		_http.Dispose();
		_logger.Dispose();
		if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
	}

	private CloudSaveManager CreateManager(string data = "cloud-saves", string? exclude = null)
	{
		var storage = CreateStorage();
		var game = CreateGame("{installdir}/Saves", exclude: exclude);
		storage.SaveMetaData(game);
		storage.AddToLocalAppState(AppName, game.LocalAppState!);
		var auth = new AuthManager(_logger, storage, new TestCredentialProtector(), _http);
		var library = new LibraryManager(_logger, new MetadataRepository(), storage, auth);
		var settings = new SettingsManager(storage, NullLogger<SettingsManager>.Instance, _root, _root);
		return new CloudSaveManager(
			_logger,
			_cloud,
			library,
			settings,
			() => Task.FromResult<string?>(Account),
			Path.Combine(_root, data),
			Folders());
	}

	private Storage CreateStorage() => new(_logger, Path.Combine(_root, "data"), Path.Combine(_root, "games"));

	private CloudSaveKnownFolders Folders() => new(
		Path.Combine(_root, "local"),
		Path.Combine(_root, "documents"),
		Path.Combine(_root, "profile"),
		Path.Combine(_root, "saved"));

	private Game CreateGame(string template, string? include = null, string? exclude = null) => new()
	{
		AppName = AppName,
		AppTitle = AppName,
		AssetInfos = new AssetInfos
		{
			Windows = new Asset { AppName = AppName, BuildVersion = "1", CatalogItemId = "item", Namespace = "ns" }
		},
		Metadata = new Metadata
		{
			Title = AppName,
			KeyImages = [],
			CustomAttributes = new CustomAttributes
			{
				CloudSaveFolder = new CloudSaveAttribute { Value = template },
				CloudIncludeList = include is null ? null : new CloudSaveAttribute { Value = include },
				CloudExcludeList = exclude is null ? null : new CloudSaveAttribute { Value = exclude }
			}
		},
		MetadataSchema = Game.CurrentMetadataSchema,
		LocalAppState = new LocalAppState
		{
			AppName = AppName,
			InstallPath = InstallPath,
			InstallStatus = InstallState.Installed
		}
	};

	private void WriteSave(string name, string content)
	{
		Directory.CreateDirectory(SaveFolder);
		File.WriteAllText(Path.Combine(SaveFolder, name), content);
	}

	private string ReadSave(string name) => File.ReadAllText(Path.Combine(SaveFolder, name), Encoding.UTF8);

	private sealed class FakeCloud : ICloudSaveRepository
	{
		public ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
		public List<string> WriteOrder { get; } = [];

		public Task<IReadOnlyDictionary<string, CloudSaveRemoteFile>> ListFilesAsync(string accountId, string appName, CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyDictionary<string, CloudSaveRemoteFile>>(
				Files.Keys.ToDictionary(key => key, key => new CloudSaveRemoteFile(key, null), StringComparer.Ordinal));

		public Task<IReadOnlyDictionary<string, CloudSaveRemoteFile>> CreateFilesAsync(string accountId, string appName, IReadOnlyList<string> remotePaths, CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyDictionary<string, CloudSaveRemoteFile>>(remotePaths.ToDictionary(
				path => path,
				path => new CloudSaveRemoteFile(null, $"savesync/{accountId}/{appName}/{path}"),
				StringComparer.Ordinal));

		public Task<byte[]> DownloadAsync(string readLink, CancellationToken cancellationToken) =>
			Task.FromResult(Files[readLink].ToArray());

		public async Task UploadFileAsync(string writeLink, string localFile, CancellationToken cancellationToken)
		{
			Files[writeLink] = await File.ReadAllBytesAsync(localFile, cancellationToken);
			lock (WriteOrder)
				WriteOrder.Add(writeLink);
		}
	}

	private sealed class MetadataRepository : IStoreRepository
	{
		public int MetadataRequests;

		public Task<IEnumerable<Asset>> FetchGameAssets(string platform = "Windows", string label = "Live") =>
			Task.FromResult<IEnumerable<Asset>>([new Asset { AppName = AppName, Namespace = "ns", CatalogItemId = "item", BuildVersion = "1" }]);

		public Task<Metadata> FetchGameMetaData(string nameSpace, string catalogItemId)
		{
			Interlocked.Increment(ref MetadataRequests);
			return Task.FromResult(new Metadata
			{
				Id = catalogItemId,
				Title = AppName,
				KeyImages = [],
				CustomAttributes = new CustomAttributes { CloudSaveFolder = new CloudSaveAttribute { Value = "{installdir}/Saves" } }
			});
		}

		public Task<byte[]> GetGameManifest(GetManifestUrlData data, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DownloadFileAsync(string url, string destinationPath) => throw new NotSupportedException();
		public Task<string> GetGameToken() => throw new NotSupportedException();
		public Task<byte[]?> GetOwnershipToken(string nameSpace, string catalogItemId) => throw new NotSupportedException();
		public Task<GetManifestUrlData> GetManifestUrls(string nameSpace, string catalogItem, string appName, string platform = "Windows", string label = "Live", CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}
}
