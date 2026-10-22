using System.Text;
using Crimson.Core;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Crimson.ViewModels;
using Serilog;

namespace Crimson.Tests;

public sealed class AppInstallDialogViewModelTests : IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(),
		$"crimson-install-dialog-{Guid.NewGuid():N}");
	private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

	[Fact]
	public async Task OlderInitializationCannotOverwriteNewerGame()
	{
		var repository = new ControlledManifestRepository();
		var storage = new Storage(_logger, _root, Path.Combine(_root, "games"));
		var firstGame = CreateGame("first", "First", "first-box");
		var secondGame = CreateGame("second", "Second", "second-box");
		storage.SaveMetaData(firstGame);
		storage.SaveMetaData(secondGame);
		var library = CreateLibrary(storage, repository);
		var installer = new InstallManager(
			_logger,
			library,
			repository,
			storage,
			new DownloadManager(_logger, new HttpClient()),
			new UnusedShortcutManager(),
			new AllowInstallPermissionChecker());
		var viewModel = new AppInstallDialogViewModel(
			_logger,
			installer,
			library,
			storage,
			new ImmediateUiDispatcher());

		var firstInitialization = viewModel.InitializeAsync(firstGame);
		var secondInitialization = viewModel.InitializeAsync(secondGame);
		repository.Complete("second", CreateJsonManifest(downloadBytes: 200, writeBytes: 20));
		await secondInitialization;
		repository.Complete("first", CreateJsonManifest(downloadBytes: 100, writeBytes: 10));
		await firstInitialization;

		Assert.Equal("Second", viewModel.GameTitle);
		Assert.Equal("second-box", viewModel.GameImageUrl);
		Assert.Equal("200 B", viewModel.TotalDownloadSize);
		Assert.Equal("20 B", viewModel.TotalInstallSize);
		Assert.False(viewModel.IsLoadingContent);
	}

	[Fact]
	public async Task ObsoleteInitializationCannotInvalidateCurrentDriveRequest()
	{
		var repository = new ControlledManifestRepository();
		var volume = new BlockingVolumeResolver();
		var dispatcher = new BlockingFirstDispatcher();
		var storage = new Storage(_logger, _root, Path.Combine(_root, "games"), volume);
		var first = CreateGame("first", "First", "first-box");
		var second = CreateGame("second", "Second", "second-box");
		storage.SaveMetaData(first);
		storage.SaveMetaData(second);
		var library = CreateLibrary(storage, repository);
		var installer = new InstallManager(_logger, library, repository, storage,
			new DownloadManager(_logger, new HttpClient()), new UnusedShortcutManager(), new AllowInstallPermissionChecker());
		var vm = new AppInstallDialogViewModel(_logger, installer, library, storage, dispatcher);

		var oldInitialization = vm.InitializeAsync(first);
		repository.Complete("first", CreateJsonManifest(100, 10));
		await dispatcher.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
		var currentInitialization = vm.InitializeAsync(second);
		repository.Complete("second", CreateJsonManifest(200, 20));
		try
		{
			await volume.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
			dispatcher.Release.Set();
			await oldInitialization.WaitAsync(TimeSpan.FromSeconds(5));
		}
		finally
		{
			dispatcher.Release.Set();
			volume.Release.Set();
		}
		await currentInitialization.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.False(vm.IsLoadingContent);
		Assert.True(vm.IsDriveSpaceVisible);
		Assert.Equal("Second", vm.GameTitle);
		Assert.Equal("20 B", vm.TotalInstallSize);
	}

	[Fact]
	public async Task CancellationReleasesBlockedManifestInitialization()
	{
		var repository = new ControlledManifestRepository();
		var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		repository.ManifestRequested += _ => requested.TrySetResult();
		var storage = new Storage(_logger, _root, Path.Combine(_root, "games"));
		var game = CreateGame("game", "Game", "box");
		storage.SaveMetaData(game);
		var library = CreateLibrary(storage, repository);
		var installer = new InstallManager(_logger, library, repository, storage,
			new DownloadManager(_logger, new HttpClient()), new UnusedShortcutManager(), new AllowInstallPermissionChecker());
		var viewModel = new AppInstallDialogViewModel(
			_logger, installer, library, storage, new ImmediateUiDispatcher());
		var requestedClose = false;
		viewModel.RequestClose += () => requestedClose = true;
		using var cancellation = new CancellationTokenSource();

		var initialization = viewModel.InitializeAsync(game, cancellation.Token);
		await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
		cancellation.Cancel();
		await initialization.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.False(requestedClose);
	}

	[Fact]
	public async Task FailedMoveCannotCompleteNextUninstallBeforeItsManifestReturns()
	{
		var repository = new ControlledManifestRepository();
		var storage = new Storage(_logger, _root, Path.Combine(_root, "games"));
		var source = Path.Combine(_root, "source");
		var target = Path.Combine(_root, "uninstall");
		Directory.CreateDirectory(source);
		Directory.CreateDirectory(target);
		var file = Path.Combine(target, "game.exe");
		File.WriteAllText(file, "synthetic game data");
		var move = CreateGame("move", "Move", "image");
		var uninstall = CreateGame("uninstall", "Uninstall", "image");
		foreach (var (game, path) in new[] { (move, source), (uninstall, target) })
		{
			game.LocalAppState = new LocalAppState {
				AppName = game.AppName, InstallPath = path, InstallStatus = InstallState.Installed
			};
			storage.SaveMetaData(game);
			storage.AddToLocalAppState(game.AppName, game.LocalAppState);
		}
		var library = CreateLibrary(storage, repository);
		var installer = new InstallManager(_logger, library, repository, storage,
			new DownloadManager(_logger, new HttpClient()), new UnusedShortcutManager(), new AllowInstallPermissionChecker());
		var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var success = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		repository.ManifestRequested += app => { if (app == "uninstall") requested.TrySetResult(); };
		installer.InstallationStatusChanged += item => {
			if (item.AppName == "uninstall" && item.Status == ActionStatus.Success) success.TrySetResult();
		};
		installer.AddToQueue(new InstallItem("move", ActionType.Move, source) {
			MoveLocation = Path.Combine(_root, "missing-parent", "destination")
		});
		installer.AddToQueue(new InstallItem("uninstall", ActionType.Uninstall, target));
		repository.Complete("move", CreateJsonManifest(100, 10));
		await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await Task.WhenAny(success.Task, Task.Delay(3000));
		var completedEarly = success.Task.IsCompleted;
		repository.Complete("uninstall", CreateJsonManifest(100, 10));
		await success.Task.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.False(completedEarly);
		Assert.False(File.Exists(file));
		Assert.Equal(InstallState.NotInstalled, uninstall.LocalAppState!.InstallStatus);
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
			Directory.Delete(_root, recursive: true);
	}

	private LibraryManager CreateLibrary(Storage storage, IStoreRepository repository)
	{
		var auth = new AuthManager(
			_logger,
			storage,
			new TestCredentialProtector(),
			new HttpClient());
		return new LibraryManager(_logger, repository, storage, auth);
	}

	private static Game CreateGame(string appName, string title, string imageUrl) => new()
	{
		AppName = appName,
		AppTitle = title,
		AssetInfos = new AssetInfos
		{
			Windows = new Asset
			{
				AppName = appName,
				BuildVersion = "1.0",
				CatalogItemId = appName,
				Namespace = "test"
			}
		},
		Metadata = new Metadata
		{
			Id = appName,
			KeyImages = [new KeyImage { Type = "DieselGameBox", Url = imageUrl }]
		}
	};

	private static byte[] CreateJsonManifest(int downloadBytes, int writeBytes)
	{
		const string guid = "00000001000000020000000300000004";
		var json = $$"""
			{
			  "FileManifestList":[{
				"Filename":"game.exe",
				"FileHash":"{{EncodeDecimalBytes(new byte[20])}}",
				"FileChunkParts":[{
				  "Guid":"{{guid}}",
				  "Offset":"{{EncodeDecimalBytes(BitConverter.GetBytes(0))}}",
				  "Size":"{{EncodeDecimalBytes(BitConverter.GetBytes(writeBytes))}}"
				}]
			  }],
			  "ChunkHashList":{"{{guid}}":"{{EncodeDecimalBytes(BitConverter.GetBytes(0L))}}"},
			  "ChunkShaList":{"{{guid}}":"{{new string('0', 40)}}"},
			  "DataGroupList":{"{{guid}}":"{{EncodeDecimalBytes([8])}}"},
			  "ChunkFilesizeList":{"{{guid}}":"{{EncodeDecimalBytes(BitConverter.GetBytes((long)downloadBytes))}}"}
			}
			""";
		return Encoding.UTF8.GetBytes(json);
	}

	private static string EncodeDecimalBytes(IEnumerable<byte> bytes) =>
		string.Concat(bytes.Select(value => value.ToString("D3")));

	private sealed class ImmediateUiDispatcher : IUiDispatcher
	{
		public bool TryEnqueue(Action callback)
		{
			callback();
			return true;
		}
	}

	private sealed class BlockingFirstDispatcher : IUiDispatcher
	{
		private int _calls;
		public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public ManualResetEventSlim Release { get; } = new(false);
		public bool TryEnqueue(Action callback)
		{
			if (Interlocked.Increment(ref _calls) == 1)
			{
				Blocked.TrySetResult();
				if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Dispatcher barrier timed out.");
			}
			callback();
			return true;
		}
	}

	private sealed class BlockingVolumeResolver : IFileSystemVolumeResolver
	{
		private int _calls;
		public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public ManualResetEventSlim Release { get; } = new(false);
		public DriveInfo GetVolume(string path)
		{
			if (Interlocked.Increment(ref _calls) == 1)
			{
				Blocked.TrySetResult();
				if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Volume barrier timed out.");
			}
			return new DriveInfo(Path.GetPathRoot(path)!);
		}
		public bool AreOnSameVolume(string firstPath, string secondPath) => true;
	}

	private sealed class AllowInstallPermissionChecker : IInstallPermissionChecker
	{
		public InstallPermissionCheckResult Check(string folderPath) => new(true);
	}

	private sealed class UnusedShortcutManager : IGameShortcutManager
	{
		public Task CreateAsync(Game game, GameShortcutLocation location, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public void Remove(Game game)
		{
		}
	}

	private sealed class ControlledManifestRepository : IStoreRepository
	{
		private readonly Dictionary<string, TaskCompletionSource<byte[]>> _manifests = new();

		public void Complete(string appName, byte[] manifest) =>
			GetSource(appName).SetResult(manifest);

		public Task<GetManifestUrlData> GetManifestUrls(
			string nameSpace,
			string catalogItem,
			string appName,
			string platform = "Windows",
			string label = "Live",
			CancellationToken cancellationToken = default) => Task.FromResult(new GetManifestUrlData
			{
				BaseUrls = [],
				ManifestUrls = [appName],
				ManifestHash = string.Empty
			});

		public event Action<string>? ManifestRequested;

		public async Task<byte[]> GetGameManifest(
			GetManifestUrlData urlData,
			CancellationToken cancellationToken = default)
		{
			var appName = urlData.ManifestUrls[0];
			var source = GetSource(appName);
			ManifestRequested?.Invoke(appName);
			return await source.Task.WaitAsync(cancellationToken);
		}

		private TaskCompletionSource<byte[]> GetSource(string appName)
		{
			lock (_manifests)
			{
				if (!_manifests.TryGetValue(appName, out var source))
				{
					source = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
					_manifests.Add(appName, source);
				}

				return source;
			}
		}

		public Task<Metadata> FetchGameMetaData(string nameSpace, string catalogItemId) =>
			throw new NotSupportedException();

		public Task<IEnumerable<Asset>> FetchGameAssets(string platform = "Windows", string label = "Live") =>
			throw new NotSupportedException();

		public Task DownloadFileAsync(string url, string destinationPath) =>
			throw new NotSupportedException();

		public Task<string> GetGameToken() => throw new NotSupportedException();

		public Task<byte[]?> GetOwnershipToken(string nameSpace, string catalogItemId) =>
			throw new NotSupportedException();
	}
}
