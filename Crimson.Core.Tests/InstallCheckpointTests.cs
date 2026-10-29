using System.Reflection;
using Crimson.Core;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Serilog;

namespace Crimson.Tests;

public sealed class InstallCheckpointTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"crimson-checkpoint-{Guid.NewGuid():N}");
	private readonly string _gameRoot;
	private readonly HttpClient _http = new();
	private readonly Storage _storage;
	private readonly InstallManager _installer;
	private readonly StubManifestRepository _repository = new();
	private readonly Serilog.Core.Logger _logger = new LoggerConfiguration().CreateLogger();

	public InstallCheckpointTests()
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
		_installer = BuildInstaller();
	}

	[Fact]
	public void SaveActiveInstallStateWithoutActiveInstallWritesNothing()
	{
		_installer.SaveActiveInstallState();

		Assert.Null(_storage.GetInstallState("test"));
	}

	[Fact]
	public void SaveActiveInstallStateSnapshotsTheProcessingInstall()
	{
		SetCurrentInstall(new InstallItem("test", ActionType.Install, _gameRoot)
		{
			Status = ActionStatus.Processing
		});

		_installer.SaveActiveInstallState();

		Assert.NotNull(_storage.GetInstallState("test"));
	}

	[Fact]
	public async Task CheckpointedDownloadIsRestoredAsPausedAfterRestart()
	{
		SetCurrentInstall(new InstallItem("test", ActionType.Install, _gameRoot)
		{
			Status = ActionStatus.Processing,
			WrittenSizeMiB = 7,
			TotalWriteSizeMb = 100
		});
		_installer.SaveActiveInstallState();

		var restarted = BuildInstaller();
		await restarted.LoadPendingInstalls();

		Assert.NotNull(restarted.CurrentInstall);
		Assert.Equal("test", restarted.CurrentInstall!.AppName);
		Assert.Equal(ActionStatus.Paused, restarted.CurrentInstall.Status);
	}

	[Fact]
	public async Task FailedInstallKeepsItsSnapshotForTheNextStart()
	{
		SaveSnapshotOfProcessingInstall();

		await InvokeStoppage("Download failed: connection lost");

		Assert.NotNull(_storage.GetInstallState("test"));
	}

	[Fact]
	public async Task CancelledInstallDiscardsItsSnapshot()
	{
		SaveSnapshotOfProcessingInstall();
		SetUserCancelled();

		await InvokeStoppage("Cancel install");

		Assert.Null(_storage.GetInstallState("test"));
	}

	public void Dispose()
	{
		_http.Dispose();
		_logger.Dispose();
		try
		{
			if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
		}
	}

	private InstallManager BuildInstaller()
	{
		var library = new LibraryManager(
			_logger,
			_repository,
			_storage,
			new AuthManager(_logger, _storage, new UnprotectableCredentialProtector(), _http));
		return new InstallManager(
			_logger,
			library,
			_repository,
			_storage,
			new DownloadManager(_logger, _http),
			null!,
			new AllowInstallPermissionChecker());
	}

	private void SaveSnapshotOfProcessingInstall()
	{
		SetCurrentInstall(new InstallItem("test", ActionType.Install, _gameRoot)
		{
			Status = ActionStatus.Processing
		});
		_installer.SaveActiveInstallState();
	}

	private void SetCurrentInstall(InstallItem install) =>
		typeof(InstallManager)
			.GetProperty(nameof(InstallManager.CurrentInstall))!
			.SetValue(_installer, install);

	private void SetUserCancelled() =>
		typeof(InstallManager)
			.GetField("_userCancelled", BindingFlags.Instance | BindingFlags.NonPublic)!
			.SetValue(_installer, true);

	private Task InvokeStoppage(string errorMessage) =>
		(Task)typeof(InstallManager)
			.GetMethod("HandleInstallationStoppage", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(_installer, [errorMessage])!;

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
		public Task<GetManifestUrlData> GetManifestUrls(string nameSpace, string catalogItem, string appName, string platform = "Windows", string label = "Live", CancellationToken cancellationToken = default) =>
			Task.FromResult(new GetManifestUrlData { BaseUrls = [], ManifestUrls = ["test"], ManifestHash = string.Empty });

		public Task<byte[]> GetGameManifest(GetManifestUrlData data, CancellationToken cancellationToken = default) =>
			Task.FromResult(System.Text.Encoding.UTF8.GetBytes("""
				{"FileManifestList":[],"ChunkHashList":{},"ChunkShaList":{},"DataGroupList":{},"ChunkFilesizeList":{}}
				"""));

		public Task<Metadata> FetchGameMetaData(string nameSpace, string catalogItemId) => throw new NotSupportedException();
		public Task<IEnumerable<Asset>> FetchGameAssets(string platform = "Windows", string label = "Live") => throw new NotSupportedException();
		public Task DownloadFileAsync(string url, string destinationPath) => throw new NotSupportedException();
		public Task<string> GetGameToken() => throw new NotSupportedException();
		public Task<byte[]?> GetOwnershipToken(string nameSpace, string catalogItemId) => throw new NotSupportedException();
	}
}
