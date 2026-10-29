using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Crimson.Core;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Serilog;

namespace Crimson.Tests;

public sealed class InstallResumeProgressTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"crimson-resume-progress-{Guid.NewGuid():N}");
	private readonly string _gameRoot;
	private readonly HttpClient _http = new();
	private readonly Storage _storage;
	private readonly InstallManager _installer;
	private readonly StubManifestRepository _repository = new();
	private readonly Serilog.Core.Logger _logger = new LoggerConfiguration().CreateLogger();


	public InstallResumeProgressTests()
	{
		_gameRoot = Path.Combine(_root, "game");
		Directory.CreateDirectory(_gameRoot);
		_storage = new Storage(_logger, Path.Combine(_root, "data"), _gameRoot);
		var game = new Game
		{
			AppName = "test",
			AppTitle = "Test Game",
			AssetInfos = new AssetInfos
			{
				Windows = new Asset { AppName = "test", Namespace = "test", CatalogItemId = "test", BuildVersion = "1.0" }
			},
			Metadata = new Metadata { Id = "test", KeyImages = [] },
			LocalAppState = new LocalAppState { AppName = "test", InstallStatus = InstallState.NotInstalled }
		};
		_storage.SaveMetaData(game);
		_storage.AddToLocalAppState(game.AppName, game.LocalAppState);
		var library = new LibraryManager(
			_logger,
			_repository,
			_storage,
			new AuthManager(_logger, _storage, new UnprotectableCredentialProtector(), _http));
		_installer = new InstallManager(
			_logger,
			library,
			_repository,
			_storage,
			new DownloadManager(_logger, _http),
			null!,
			new AllowInstallPermissionChecker());
	}

	[Fact]
	public async Task RestartReclaimsFilesAlreadyCompleteOnDisk()
	{
		// Two files are already correct on disk; the provider still lists all three as missing.
		// This is the state a pause snapshot can be left in, and the restart must not requeue
		// the bytes that are already written.
		var done = CreateChunk(1);
		var pending = CreateChunk(2);
		var other = CreateChunk(3);
		WriteFile("done.bin", done, "done.chunk");
		_repository.Manifest = BuildManifest(
			("done.bin", [done]),
			("pending.bin", [pending]),
			("other.bin", [other]));
		CacheManifest();
		SaveState(new InstallItem("test", ActionType.Install, _gameRoot)
		{
			WrittenSizeMiB = 1,
			TotalWriteSizeMb = 3
		});

		await _installer.LoadPendingInstalls();

		var restored = _installer.CurrentInstall;
		Assert.NotNull(restored);
		Assert.Equal(ActionStatus.Paused, restored.Status);
		// done.bin is reclaimed from disk; the other two still need their chunks.
		Assert.Equal(2, GetDownloadQueue().Count);
		var queue = GetDownloadQueue().Select(task => task.ChunkInfo.GuidNum).ToHashSet();
		Assert.Contains(ChunkOf(pending), queue);
		Assert.Contains(ChunkOf(other), queue);
		Assert.DoesNotContain(ChunkOf(done), queue);
		// The reclaimed file counts as progress; the two pending files keep the bar below 100%.
		Assert.True(restored.WrittenSizeMiB > 0, "the reclaimed file should count as progress");
		Assert.True(restored.ProgressPercentage is > 0 and < 100,
			$"progress {restored.ProgressPercentage}% should be partial");
	}

	[Fact]
	public async Task RestartTotalsMatchTheManifestWhenTheSnapshotLagsTheDisk()
	{
		// The snapshot recorded one file but the disk holds two and no completed chunks were
		// saved. The total must stay the manifest size rather than counting a file twice.
		var first = CreateChunk(1);
		var second = CreateChunk(2);
		var pending = CreateChunk(3);
		WriteFile("first.bin", first, "first.chunk");
		WriteFile("second.bin", second, "second.chunk");
		_repository.Manifest = BuildManifest(
			("first.bin", [first]),
			("second.bin", [second]),
			("pending.bin", [pending]));
		CacheManifest();
		const double fileMiB = 64 / 1024.0 / 1024.0;
		SaveState(new InstallItem("test", ActionType.Install, _gameRoot)
		{
			WrittenSizeMiB = fileMiB,
			TotalWriteSizeMb = 5 * fileMiB
		});

		await _installer.LoadPendingInstalls();

		var restored = _installer.CurrentInstall;
		Assert.NotNull(restored);
		Assert.Equal(2 * fileMiB, restored.WrittenSizeMiB, precision: 9);
		Assert.Equal(3 * fileMiB, restored.TotalWriteSizeMb, precision: 9);
		Assert.Equal(67, restored.ProgressPercentage);
	}

	[Fact]
	public async Task RestartSavesTheVerifiedProgressAndResetsCarriedOverSpeeds()
	{
		// The snapshot lags the disk and carries speeds from an earlier session. Once the disk
		// check has run, the saved state must hold its figures so the next start (and the paused
		// list) does not fall back to the stale ones.
		var first = CreateChunk(1);
		var second = CreateChunk(2);
		var pending = CreateChunk(3);
		WriteFile("first.bin", first, "first.chunk");
		WriteFile("second.bin", second, "second.chunk");
		_repository.Manifest = BuildManifest(
			("first.bin", [first]),
			("second.bin", [second]),
			("pending.bin", [pending]));
		CacheManifest();
		const double fileMiB = 64 / 1024.0 / 1024.0;
		SaveState(new InstallItem("test", ActionType.Install, _gameRoot)
		{
			WrittenSizeMiB = fileMiB,
			TotalWriteSizeMb = 3 * fileMiB,
			ProgressPercentage = 33,
			DownloadSpeedRawMiB = 9384.68,
			WriteSpeedMiB = 19850.14
		});

		await _installer.LoadPendingInstalls();

		var saved = JsonSerializer.Deserialize<InstallManagerState>(_storage.GetInstallState("test")!)!.CurrentInstall;
		Assert.Equal(67, saved.ProgressPercentage);
		Assert.Equal(2 * fileMiB, saved.WrittenSizeMiB, precision: 9);
		Assert.Equal(0, saved.DownloadSpeedRawMiB);
		Assert.Equal(0, saved.WriteSpeedMiB);
		// The legacy snapshot was hashed once; from now on the ledger vouches for those files.
		var ledger = JsonSerializer.Deserialize<InstallManagerState>(_storage.GetInstallState("test")!)!.CompletedFiles;
		Assert.Equal(["first.bin", "second.bin"], ledger!.Select(record => record.Filename).Order());
	}

	[Fact]
	public async Task RestartTrustsTheLedgerWithoutHashingUntouchedFiles()
	{
		// done.bin holds the wrong bytes on purpose: the ledger vouches for it, so it must not be
		// hashed again. touched.bin changed after it was recorded and has to be written again.
		var done = CreateChunk(1);
		var touched = CreateChunk(2);
		_repository.Manifest = BuildManifest(("done.bin", [done]), ("touched.bin", [touched]));
		CacheManifest();
		var files = Manifest.ReadAll(_repository.Manifest).FileManifestList.Elements;
		var donePath = WriteGameFile("done.bin", CreateChunk(9));
		var touchedPath = WriteGameFile("touched.bin", touched);
		var records = new List<CompletedFileRecord>
		{
			CompletedFileRecord.From(files[0], new FileInfo(donePath)),
			CompletedFileRecord.From(files[1], new FileInfo(touchedPath))
		};
		File.SetLastWriteTimeUtc(touchedPath, File.GetLastWriteTimeUtc(touchedPath).AddMinutes(1));
		SaveLedgerState(new InstallItem("test", ActionType.Install, _gameRoot), records);

		await _installer.LoadPendingInstalls();

		Assert.Equal([ChunkOf(touched)], GetDownloadQueue().Select(task => task.ChunkInfo.GuidNum));
		Assert.Equal(50, _installer.CurrentInstall!.ProgressPercentage);
	}

	[Fact]
	public async Task RestartDownloadsOnlyThePartsAFileIsStillMissing()
	{
		// The first half of big.bin was written before the pause; only the second chunk is
		// fetched, and it is copied to the second half only.
		var first = CreateChunk(1);
		var second = CreateChunk(2);
		_repository.Manifest = BuildManifest(("big.bin", [first, second]));
		CacheManifest();
		var file = Manifest.ReadAll(_repository.Manifest).FileManifestList.Elements[0];
		WriteGameFile("big.bin", [.. first, .. new byte[second.Length]]);
		SaveLedgerState(
			new InstallItem("test", ActionType.Install, _gameRoot),
			completed: [],
			partial: [new PartialFileRecord { Filename = "big.bin", Sha = Convert.ToHexString(file.ShaHash), FileOffsets = [0] }]);

		await _installer.LoadPendingInstalls();

		var queue = GetDownloadQueue();
		Assert.Equal([ChunkOf(second)], queue.Select(task => task.ChunkInfo.GuidNum));
		Assert.Equal(50, _installer.CurrentInstall!.ProgressPercentage);
		var copies = CreateIoTasks(queue[0]);
		Assert.Equal([(long)first.Length], copies.Select(task => task.FileOffset));
	}

	[Fact]
	public async Task RestoredDownloadsCanBeCopiedToTheirFiles()
	{
		// The restored plan has to map every queued chunk to its files; a chunk missing from the
		// map made the first download after a resume fail.
		var done = CreateChunk(1);
		var pending = CreateChunk(2);
		WriteFile("done.bin", done, "done.chunk");
		_repository.Manifest = BuildManifest(("done.bin", [done]), ("pending.bin", [pending]));
		CacheManifest();
		SaveState(new InstallItem("test", ActionType.Install, _gameRoot));

		await _installer.LoadPendingInstalls();

		var queue = GetDownloadQueue();
		Assert.Single(queue);
		var copy = Assert.Single(CreateIoTasks(queue[0]));
		Assert.EndsWith("pending.bin", copy.DestinationFilePath);
	}

	[Fact]
	public async Task WritingTheLastPartOfAFileRecordsItInTheLedger()
	{
		var chunk = CreateChunk(1);
		_repository.Manifest = BuildManifest(("pending.bin", [chunk]));
		CacheManifest();
		SaveLedgerState(new InstallItem("test", ActionType.Install, _gameRoot), completed: []);
		await _installer.LoadPendingInstalls();
		var path = WriteGameFile("pending.bin", chunk);

		InvokePrivate("RecordPartWritten", new IoTask { DestinationFilePath = path, FileOffset = 0 });

		var state = (InstallManagerState)InvokePrivate("CaptureState", _installer.CurrentInstall!)!;
		var record = Assert.Single(state.CompletedFiles!);
		Assert.Equal("pending.bin", record.Filename);
		Assert.Equal(File.GetLastWriteTimeUtc(path).Ticks, record.LastWriteUtcTicks);
		Assert.Empty(state.WrittenParts!);
	}

	[Fact]
	public async Task RestartReportsVerifyingUntilTheDiskCheckFinishes()
	{
		// The snapshot's progress is unverified, so views are told not to show it until the
		// check of the disk has produced the real figures.
		var chunk = CreateChunk(1);
		_repository.Manifest = BuildManifest(("pending.bin", [chunk]));
		CacheManifest();
		SaveState(new InstallItem("test", ActionType.Install, _gameRoot)
		{
			WrittenSizeMiB = 1,
			TotalWriteSizeMb = 2,
			ProgressPercentage = 50
		});
		var verifying = new List<bool>();
		_installer.InstallationStatusChanged += item =>
		{
			if (item is not null) verifying.Add(item.IsVerifying);
		};

		await _installer.LoadPendingInstalls();

		Assert.True(verifying.Count >= 2);
		Assert.True(verifying[0], "the first notification must mark the install as verifying");
		Assert.False(verifying[^1], "the final notification must carry verified figures");
		Assert.False(_installer.CurrentInstall!.IsVerifying);
	}

	[Fact]
	public async Task RestartRequeuesFilesThatDoNotMatchTheManifest()
	{
		// The file is on disk but holds different bytes than the manifest describes, so it must
		// be downloaded again rather than counted as progress.
		var chunk = CreateChunk(1);
		File.WriteAllBytes(Path.Combine(_gameRoot, "pending.bin"), CreateChunk(9));
		_repository.Manifest = BuildManifest(("pending.bin", [chunk]));
		CacheManifest();
		SaveState(new InstallItem("test", ActionType.Install, _gameRoot));

		await _installer.LoadPendingInstalls();

		var restored = _installer.CurrentInstall;
		Assert.NotNull(restored);
		Assert.Equal(1, GetDownloadQueue().Count);
		Assert.Contains(ChunkOf(chunk), GetDownloadQueue().Select(task => task.ChunkInfo.GuidNum));
		Assert.Equal(0, restored.ProgressPercentage);
	}

	[Fact]
	public void WriteProgressAddsOnlyTheNewBytes()
	{
		var install = new InstallItem("test", ActionType.Install, _root)
		{
			Status = ActionStatus.Processing,
			WrittenSizeMiB = 40,
			TotalWriteSizeMb = 100
		};
		typeof(InstallManager)
			.GetProperty(nameof(InstallManager.CurrentInstall))!
			.SetValue(_installer, install);

		typeof(InstallManager)
			.GetMethod("UpdateInstallWriteProgress", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
			.Invoke(_installer, [10L * 1024 * 1024]);

		Assert.Equal(50, install.WrittenSizeMiB, precision: 6);
		Assert.Equal(50, install.ProgressPercentage);
	}

	public void Dispose()
	{
		_http.Dispose();
		_logger.Dispose();
		//if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
	}

	[Fact]
	public async Task CancellingAnInstallRemovesItsFilesAndFolder()
	{
		_repository.Manifest = BuildManifest(("a.bin", [CreateChunk(1)]), ("sub/b.bin", [CreateChunk(2)]));
		WriteGameFile("a.bin", CreateChunk(1));
		Directory.CreateDirectory(Path.Combine(_gameRoot, "sub"));
		WriteGameFile(Path.Combine("sub", "b.bin"), CreateChunk(2));
		Directory.CreateDirectory(Path.Combine(_gameRoot, ".Crimson"));

		await CancelInstall(new InstallItem("test", ActionType.Install, _gameRoot));

		Assert.False(Directory.Exists(_gameRoot));
		Assert.Empty(_installer.GetHistoryItemsNames());
	}

	[Fact]
	public async Task CancellingAnInstallKeepsFilesTheManifestDoesNotList()
	{
		_repository.Manifest = BuildManifest(("a.bin", [CreateChunk(1)]));
		WriteGameFile("a.bin", CreateChunk(1));
		var unrelated = WriteGameFile("notes.txt", [1, 2, 3]);

		await CancelInstall(new InstallItem("test", ActionType.Install, _gameRoot));

		Assert.False(File.Exists(Path.Combine(_gameRoot, "a.bin")));
		Assert.True(File.Exists(unrelated));
	}

	private Task CancelInstall(InstallItem install)
	{
		install.Status = ActionStatus.Processing;
		typeof(InstallManager)
			.GetProperty(nameof(InstallManager.CurrentInstall))!
			.SetValue(_installer, install);
		typeof(InstallManager)
			.GetField("_userCancelled", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
			.SetValue(_installer, true);
		return (Task)InvokePrivate("HandleInstallationStoppage", "Cancel install")!;
	}

	private List<DownloadTask> GetDownloadQueue()
	{
		var queue = typeof(InstallManager)
			.GetField("_downloadQueue", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
			.GetValue(_installer)!;
		return ((System.Collections.IEnumerable)queue).Cast<DownloadTask>().ToList();
	}

	private void CacheManifest()
	{
		var directory = Path.Combine(_root, "data", "manifests");
		Directory.CreateDirectory(directory);
		File.WriteAllBytes(Path.Combine(directory, "test_1.0.manifest"), _repository.Manifest);
	}

	private void SaveState(InstallItem install) =>
		_storage.SaveInstallState(JsonSerializer.Serialize(new InstallManagerState
		{
			CurrentInstall = install,
			IoQueue = [],
			CompletedChunks = []
		}));

	private void SaveLedgerState(
		InstallItem install,
		List<CompletedFileRecord> completed,
		List<PartialFileRecord>? partial = null) =>
		_storage.SaveInstallState(install.AppName, JsonSerializer.Serialize(new InstallManagerState
		{
			CurrentInstall = install,
			CompletedFiles = completed,
			WrittenParts = partial ?? []
		}));

	private string WriteGameFile(string name, byte[] payload)
	{
		var path = Path.Combine(_gameRoot, name);
		File.WriteAllBytes(path, payload);
		return path;
	}

	private object? InvokePrivate(string name, params object[] arguments) =>
		typeof(InstallManager)
			.GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
			.Invoke(_installer, arguments);

	private List<IoTask> CreateIoTasks(DownloadTask download)
	{
		InvokePrivate("CreateIoTasksForChunk", download);
		var queue = typeof(InstallManager)
			.GetField("_ioQueue", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
			.GetValue(_installer)!;
		return ((System.Collections.IEnumerable)queue).Cast<IoTask>().ToList();
	}

	private void WriteFile(string name, byte[] payload, string chunkName)
	{
		File.WriteAllBytes(Path.Combine(_gameRoot, name), payload);
		// Leave a stale copy so the destination does not look in-flight or complete.
		File.WriteAllBytes(Path.Combine(_gameRoot, chunkName + ".chunk"), [0, 0, 0, 0]);
	}

	private static byte[] CreateChunk(byte seed) => Enumerable.Repeat(seed, 64).ToArray();

	private static System.Numerics.BigInteger ChunkOf(byte[] chunk)
	{
		var manifest = Manifest.ReadAll(BuildManifest(("x", [chunk])));
		return manifest.FileManifestList.Elements[0].ChunkParts[0].GuidNum;
	}

	private static byte[] BuildManifest(params (string Name, byte[][] Files)[] files)
	{
		var json = new StringBuilder("""
			{"ManifestFileVersion":"013000000000","AppNameString":"test","BuildVersionString":"1.0",
			"FileManifestList":[
			""");
		json.AppendJoin(',', files.Select(file => $$"""
			{"Filename":"{{file.Name}}","FileHash":"{{DecimalBytes(Sha1(file.Files[0]))}}","InstallTags":[],"FileChunkParts":[
			{{string.Join(',', file.Files.Select((chunk, index) => $$"""{"Guid":"{{GuidHex(chunk)}}","Offset":"000000000000","Size":"{{DecimalBytes(BitConverter.GetBytes(chunk.Length))}}"}"""))}}
			]}
			"""));
		json.Append("""
			],"ChunkHashList":{
			""");
		json.AppendJoin(',', files.SelectMany(file => file.Files).Select(chunk => $"\"{GuidHex(chunk)}\":\"" + DecimalBytes(BitConverter.GetBytes(64L)) + "\""));
		json.Append("""
			},"ChunkShaList":{
			""");
		json.AppendJoin(',', files.SelectMany(file => file.Files).Select(chunk => $"\"{GuidHex(chunk)}\":\"{Hex(Sha1(chunk))}\""));
		json.Append("""
			},"DataGroupList":{
			""");
		json.AppendJoin(',', files.SelectMany(file => file.Files).Select(chunk => $"\"{GuidHex(chunk)}\":\"000\""));
		json.Append("""
			},"ChunkFilesizeList":{
			""");
		json.AppendJoin(',', files.SelectMany(file => file.Files).Select(chunk => $"\"{GuidHex(chunk)}\":\"{DecimalBytes(BitConverter.GetBytes(64L))}\""));
		json.Append("}}");
		return Encoding.UTF8.GetBytes(json.ToString());
	}

	private static string GuidHex(byte[] chunk) => Hex(SHA1.HashData(chunk))[..32];

	private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

	private static byte[] Sha1(byte[] payload) => SHA1.HashData(payload);

	private static string DecimalBytes(byte[] bytes) =>
		string.Concat(bytes.Select(value => value.ToString("D3", CultureInfo.InvariantCulture)));

	private sealed class UnprotectableCredentialProtector : ICredentialProtector
	{
		public string Protect(string value) => value;
		public string Unprotect(string protectedValue) => protectedValue;
	}

	private sealed class AllowInstallPermissionChecker : IInstallPermissionChecker
	{
		public InstallPermissionCheckResult Check(string folderPath) => new(true);
	}

	private sealed class StubManifestRepository : IStoreRepository
	{
		public byte[] Manifest { get; set; } = Encoding.UTF8.GetBytes("""
			{"FileManifestList":[],"ChunkHashList":{},"ChunkShaList":{},"DataGroupList":{},"ChunkFilesizeList":{}}
			""");

		public Task<GetManifestUrlData> GetManifestUrls(string nameSpace, string catalogItem, string appName, string platform = "Windows", string label = "Live", CancellationToken cancellationToken = default) =>
			Task.FromResult(new GetManifestUrlData { BaseUrls = [], ManifestUrls = ["test"], ManifestHash = string.Empty });

		public Task<byte[]> GetGameManifest(GetManifestUrlData data, CancellationToken cancellationToken = default) =>
			Task.FromResult(Manifest);

		public Task<Metadata> FetchGameMetaData(string nameSpace, string catalogItemId) => throw new NotSupportedException();
		public Task<IEnumerable<Asset>> FetchGameAssets(string platform = "Windows", string label = "Live") => throw new NotSupportedException();
		public Task DownloadFileAsync(string url, string destinationPath) => throw new NotSupportedException();
		public Task<string> GetGameToken() => throw new NotSupportedException();
		public Task<byte[]?> GetOwnershipToken(string nameSpace, string catalogItemId) => throw new NotSupportedException();
	}
}
