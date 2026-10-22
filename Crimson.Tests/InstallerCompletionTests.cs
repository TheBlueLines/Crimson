using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Crimson.Core;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Serilog;

namespace Crimson.Tests;

public sealed class InstallerCompletionTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"crimson-install-completion-{Guid.NewGuid():N}");
	private readonly Serilog.Core.Logger _logger = new LoggerConfiguration().CreateLogger();
	private readonly HttpClient _http;
	private readonly HeldImageHandler _handler = new();
	private readonly Storage _storage;
	private readonly InstallManager _installer;
	private readonly Game _game;

	public InstallerCompletionTests()
	{
		_http = new HttpClient(_handler);
		_storage = new Storage(
			_logger,
			Path.Combine(_root, "data"),
			Path.Combine(_root, "game"));
		Directory.CreateDirectory(Path.Combine(_root, "game"));
		_game = new Game {
			AppName = "test", AppTitle = "Test Game",
			AssetInfos = new AssetInfos { Windows = new Asset {
				AppName = "test", Namespace = "test", CatalogItemId = "test", BuildVersion = "1.0"
			} },
			Metadata = new Metadata { Id = "test", KeyImages = [new KeyImage {
				Type = "DieselGameBoxTall", Url = "https://cdn1.epicgames.com/test.png"
			}] },
			LocalAppState = new LocalAppState { AppName = "test", InstallStatus = InstallState.Installed }
		};
		_storage.SaveMetaData(_game);
		_storage.AddToLocalAppState(_game.AppName, _game.LocalAppState);
		var repository = new EmptyManifestRepository();
		var library = new LibraryManager(
			_logger,
			repository,
			_storage,
			new AuthManager(_logger, _storage, new UnusedCredentialProtector(), _http));
		var shortcuts = new GameShortcutManager(_http, _logger,
			Path.Combine(_root, "desktop"), Path.Combine(_root, "start"), Path.Combine(_root, "icons"));
		_installer = new InstallManager(_logger, library, repository, _storage,
			new DownloadManager(_logger, _http), shortcuts, new AllowInstallPermissionChecker());
	}

	[Fact]
	public async Task PendingInstallRetainsShortcutOptions()
	{
		var item = NewInstall();
		_storage.SaveInstallState(JsonSerializer.Serialize(new {
			CurrentInstall = item, IoQueue = Array.Empty<IoTask>(), CompletedChunks = Array.Empty<int>()
		}));

		await _installer.LoadPendingInstalls();

		Assert.NotNull(_installer.CurrentInstall);
		Assert.True(_installer.CurrentInstall.CreateDesktopShortcut);
		Assert.True(_installer.CurrentInstall.CreateStartMenuShortcut);
		Assert.Equal(ActionStatus.Paused, _installer.CurrentInstall.Status);
	}

	[Fact]
	public async Task CancellationAfterFinalizationStartsFinishesShortcuts()
	{
		var item = NewInstall();
		typeof(InstallManager).GetProperty(nameof(InstallManager.CurrentInstall))!.SetValue(_installer, item);
		var begin = typeof(InstallManager).GetMethod("TryBeginFinalization", BindingFlags.Instance | BindingFlags.NonPublic)!;
		Assert.True((bool)begin.Invoke(_installer, [item])!);
		var method = typeof(InstallManager).GetMethod("CreateRequestedShortcutsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
		var creating = (Task)method.Invoke(_installer, [item, _game, _game.LocalAppState!])!;
		await _handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

		await _installer.StopProcessing();
		_handler.Release.TrySetResult(true);
		await creating.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(ActionStatus.Pending, item.Status);
		Assert.Equal(2, Directory.EnumerateFiles(_root, "*.lnk", SearchOption.AllDirectories).Count());
	}

	[Fact]
	public void UninstallRejectsTraversalBeforeQueueingDeletion()
	{
		var manifest = Manifest.ReadAll(Encoding.UTF8.GetBytes("""
			{"FileManifestList":[{"Filename":"../outside.bin","FileHash":"000000000000000000000000000000000000000000000000000000000000","FileChunkParts":[]}],"ChunkHashList":{},"ChunkShaList":{},"DataGroupList":{},"ChunkFilesizeList":{}}
			"""));
		var method = typeof(InstallManager).GetMethod("PrepareUninstallTasks", BindingFlags.Instance | BindingFlags.NonPublic)!;

		var exception = Assert.Throws<TargetInvocationException>(() => method.Invoke(_installer, [NewInstall(), manifest]));

		Assert.IsType<InvalidDataException>(exception.InnerException);
	}

	private InstallItem NewInstall() => new("test", ActionType.Install, Path.Combine(_root, "game")) {
		CreateDesktopShortcut = true, CreateStartMenuShortcut = true
	};

	public void Dispose()
	{
		_handler.Release.TrySetResult(true);
		_http.Dispose();
		_logger.Dispose();
		if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
	}

	private sealed class HeldImageHandler : HttpMessageHandler
	{
		public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Started.TrySetResult(true);
			await Release.Task;
			var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
			return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
		}
	}

	private sealed class UnusedCredentialProtector : ICredentialProtector
	{
		public string Protect(string value) => value;
		public string Unprotect(string protectedValue) => protectedValue;
	}

	private sealed class AllowInstallPermissionChecker : IInstallPermissionChecker
	{
		public InstallPermissionCheckResult Check(string folderPath) => new(true);
	}

	private sealed class EmptyManifestRepository : IStoreRepository
	{
		public Task<GetManifestUrlData> GetManifestUrls(string nameSpace, string catalogItem, string appName, string platform = "Windows", string label = "Live", CancellationToken cancellationToken = default) => Task.FromResult(new GetManifestUrlData {
			BaseUrls = [], ManifestUrls = ["test"], ManifestHash = string.Empty
		});
		public Task<byte[]> GetGameManifest(GetManifestUrlData data, CancellationToken cancellationToken = default) => Task.FromResult(Encoding.UTF8.GetBytes("""
			{"FileManifestList":[],"ChunkHashList":{},"ChunkShaList":{},"DataGroupList":{},"ChunkFilesizeList":{}}
			"""));
		public Task<Metadata> FetchGameMetaData(string nameSpace, string catalogItemId) => throw new NotSupportedException();
		public Task<IEnumerable<Asset>> FetchGameAssets(string platform = "Windows", string label = "Live") => throw new NotSupportedException();
		public Task<IReadOnlyList<StoreSearchResult>> SearchStore(string query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DownloadFileAsync(string url, string destinationPath) => throw new NotSupportedException();
		public Task<string> GetGameToken() => throw new NotSupportedException();
		public Task<byte[]?> GetOwnershipToken(string nameSpace, string catalogItemId) => throw new NotSupportedException();
	}
}
