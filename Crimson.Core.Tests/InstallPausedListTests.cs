using System.Text;
using System.Text.Json;
using Crimson.Core;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Serilog;

namespace Crimson.Tests;

public sealed class InstallPausedListTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"crimson-paused-{Guid.NewGuid():N}");
	private readonly string _gameRoot;
	private readonly HttpClient _http = new();
	private readonly Storage _storage;
	private readonly InstallManager _installer;
	private readonly StubManifestRepository _repository = new();
	private readonly Serilog.Core.Logger _logger = new LoggerConfiguration().CreateLogger();

	public InstallPausedListTests()
	{
		_gameRoot = Path.Combine(_root, "game");
		Directory.CreateDirectory(_gameRoot);
		_storage = new Storage(_logger, Path.Combine(_root, "data"), _gameRoot);
		AddGame("test");
		AddGame("test2");
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
	public async Task LoadRestoresFirstStateAndListsAdditionalPausedGames()
	{
		SavePerAppState("test", new InstallItem("test", ActionType.Install, _gameRoot));
		SavePerAppState("test2", new InstallItem("test2", ActionType.Install, _gameRoot));

		await _installer.LoadPendingInstalls();

		Assert.NotNull(_installer.CurrentInstall);
		Assert.Equal("test", _installer.CurrentInstall!.AppName);
		Assert.Equal(ActionStatus.Paused, _installer.CurrentInstall.Status);
		Assert.Equal(["test2"], _installer.SuspendedInstalls.Select(item => item.AppName));
		Assert.True(_installer.IsSuspended("test2"));
	}

	[Fact]
	public async Task AdditionalPausedGamesAreListedBeforeTheFirstRestoreFinishes()
	{
		SavePerAppState("test", new InstallItem("test", ActionType.Install, _gameRoot));
		SavePerAppState("test2", new InstallItem("test2", ActionType.Install, _gameRoot));
		_repository.ManifestGate = new TaskCompletionSource();

		var load = _installer.LoadPendingInstalls();
		await _repository.ManifestRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));

		Assert.True(_installer.IsSuspended("test2"));

		_repository.ManifestGate.SetResult();
		await load;
		Assert.Equal("test", _installer.CurrentInstall!.AppName);
	}

	[Fact]
	public async Task CancellingPausedGameRemovesItAndItsSnapshot()
	{
		SavePerAppState("test", new InstallItem("test", ActionType.Install, _gameRoot));
		SavePerAppState("test2", new InstallItem("test2", ActionType.Install, _gameRoot));
		await _installer.LoadPendingInstalls();
		Assert.True(_installer.IsSuspended("test2"));

		_installer.CancelInstall("test2");

		Assert.False(_installer.IsSuspended("test2"));
		Assert.Empty(_installer.SuspendedInstalls);

		await _installer.LoadPendingInstalls();
		Assert.False(_installer.IsSuspended("test2"));
	}

	[Fact]
	public async Task LegacyStateMigratesIntoPerAppStateWithoutDuplicates()
	{
		var json = JsonSerializer.Serialize(new InstallManagerState
		{
			CurrentInstall = new InstallItem("test", ActionType.Install, _gameRoot),
			IoQueue = [],
			CompletedChunks = []
		});
		_storage.SaveInstallState(json);
		_storage.SaveInstallState("test", json);

		await _installer.LoadPendingInstalls();

		Assert.NotNull(_installer.CurrentInstall);
		Assert.Equal("test", _installer.CurrentInstall!.AppName);
		Assert.Empty(_installer.SuspendedInstalls);
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

	private void AddGame(string appName)
	{
		var game = new Game
		{
			AppName = appName,
			AppTitle = "Test Game",
			AssetInfos = new AssetInfos
			{
				Windows = new Asset { AppName = appName, Namespace = "test", CatalogItemId = "test", BuildVersion = "1.0" }
			},
			Metadata = new Metadata { Id = appName, KeyImages = [] },
			LocalAppState = new LocalAppState { AppName = appName, InstallStatus = InstallState.NotInstalled }
		};
		_storage.SaveMetaData(game);
		_storage.AddToLocalAppState(game.AppName, game.LocalAppState);
	}

	private void SavePerAppState(string appName, InstallItem install) =>
		_storage.SaveInstallState(appName, JsonSerializer.Serialize(new InstallManagerState
		{
			CurrentInstall = install,
			IoQueue = [],
			CompletedChunks = []
		}));

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

		public TaskCompletionSource? ManifestGate { get; set; }
		public TaskCompletionSource ManifestRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public async Task<byte[]> GetGameManifest(GetManifestUrlData data, CancellationToken cancellationToken = default)
		{
			ManifestRequested.TrySetResult();
			if (ManifestGate is not null)
				await ManifestGate.Task;
			return Encoding.UTF8.GetBytes("""
				{"FileManifestList":[],"ChunkHashList":{},"ChunkShaList":{},"DataGroupList":{},"ChunkFilesizeList":{}}
				""");
		}

		public Task<Metadata> FetchGameMetaData(string nameSpace, string catalogItemId) => throw new NotSupportedException();
		public Task<IEnumerable<Asset>> FetchGameAssets(string platform = "Windows", string label = "Live") => throw new NotSupportedException();
		public Task DownloadFileAsync(string url, string destinationPath) => throw new NotSupportedException();
		public Task<string> GetGameToken() => throw new NotSupportedException();
		public Task<byte[]?> GetOwnershipToken(string nameSpace, string catalogItemId) => throw new NotSupportedException();
	}
}
