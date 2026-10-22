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

	[Fact]
	public void ContentDiffIgnoresDuplicatesAndRetainsExistingItems()
	{
		var (added, removed) = InstalledContentSelection.Diff(["keep", "remove", "remove"], ["keep", "add", "add"]);
		Assert.Equal(["add"], added);
		Assert.Equal(["remove"], removed);
	}

	[Fact]
	public void TagFilteringKeepsBaseAndFilesSharedBySelectedTags()
	{
		var manifest = Manifest.ReadAll(CreateContentManifest(("base", []), ("shared", ["en", "fr"]), ("optional", ["hd"]), ("required", [""])));
		Assert.Equal(["base", "shared", "required"], InstalledContentSelection.Filter(manifest.FileManifestList.Elements, ["fr"]).Select(file => file.Filename));
		Assert.Equal(4, InstalledContentSelection.Filter(manifest.FileManifestList.Elements, null).Count);
		Assert.Equal(["base", "required"], InstalledContentSelection.Filter(manifest.FileManifestList.Elements, []).Select(file => file.Filename));
	}

	[Theory]
	[InlineData(InstallState.Playing)]
	[InlineData(InstallState.Updating)]
	[InlineData(InstallState.Installing)]
	[InlineData(InstallState.Broken)]
	[InlineData(InstallState.InstallationPaused)]
	[InlineData(InstallState.NotInstalled)]
	public void ContentChangesRequireInstalledIdleGame(InstallState status)
	{
		Assert.False(InstalledContentSelection.CanModify(new LocalAppState { InstallPath = _root, InstallStatus = status }, false));
		Assert.False(InstalledContentSelection.CanModify(new LocalAppState { InstallPath = _root, InstallStatus = InstallState.Installed }, true));
		Assert.False(InstalledContentSelection.CanModify(null, false));
		Assert.False(InstalledContentSelection.CanModify(new LocalAppState { InstallStatus = InstallState.Installed }, false));
	}

	[Fact]
	public async Task ManageDialogUsesInstalledFolderAndPreselectsInstalledContent()
	{
		var (storage, library, installer, game) = await CreateContentInstallation();
		game.LocalAppState!.SelectedInstallTags = ["en"];
		storage.AddToLocalAppState(game.AppName, game.LocalAppState);
		var viewModel = new AppInstallDialogViewModel(_logger, installer, library, storage, new ImmediateUiDispatcher());
		await viewModel.InitializeAsync(game);

		Assert.True(viewModel.IsManageContent);
		Assert.Equal(game.LocalAppState.InstallPath, viewModel.InstallLocation);
		Assert.Equal("Installed", viewModel.BaseGameSize);
		Assert.Equal("Apply", viewModel.ConfirmButtonText);
		Assert.True(viewModel.AvailableInstallTags.Single(tag => tag.AppName == "en").IsSelected);
		Assert.False(viewModel.AvailableInstallTags.Single(tag => tag.AppName == "hd").IsSelected);
		Assert.True(viewModel.AvailableDlcs.Single().IsSelected);
		Assert.Equal("0 B", viewModel.TotalDownloadSize);
	}

	[Fact]
	public async Task ContentChangeRemovesOnlyDeselectedFilesAndPersistsAcrossReloadRepairAndUpdate()
	{
		var (storage, library, installer, game) = await CreateContentInstallation();
		var location = game.LocalAppState!.InstallPath;
		File.WriteAllText(Path.Combine(location, "notes.txt"), "keep user data");
		var completed = WaitForAction(installer, game.AppName);
		Assert.True(installer.TryQueueContentChange(game.AppName, ["en"], ["dlc"]));
		Assert.False(installer.CanManageContent(game.AppName));
		Assert.False(installer.TryQueueContentChange(game.AppName, [], []));
		await completed;

		Assert.False(File.Exists(Path.Combine(location, "hd.bin")));
		Assert.True(File.Exists(Path.Combine(location, "base.exe")));
		Assert.True(File.Exists(Path.Combine(location, "shared.bin")));
		Assert.True(File.Exists(Path.Combine(location, "notes.txt")));
		Assert.Equal(["en"], new Storage(_logger, _root, Path.Combine(_root, "games")).LocalAppStateDictionary[game.AppName].SelectedInstallTags);
		foreach (var action in new[] { ActionType.Repair, ActionType.Update })
		{
			completed = WaitForAction(installer, game.AppName);
			installer.AddToQueue(new InstallItem(game.AppName, action, location));
			await completed;
			Assert.False(File.Exists(Path.Combine(location, "hd.bin")));
			Assert.Equal(InstallState.Installed, game.LocalAppState.InstallStatus);
		}
	}

	[Fact]
	public async Task DlcRemovalPreservesBaseSharedFilesAndInstallDirectory()
	{
		var (storage, library, installer, game) = await CreateContentInstallation();
		var location = game.LocalAppState!.InstallPath;
		var completed = WaitForAction(installer, "dlc");
		Assert.True(installer.TryQueueContentChange(game.AppName, ["en", "hd"], []));
		await completed;

		Assert.True(Directory.Exists(location));
		Assert.True(File.Exists(Path.Combine(location, "base.exe")));
		Assert.True(File.Exists(Path.Combine(location, "shared.bin")));
		Assert.False(File.Exists(Path.Combine(location, "dlc.bin")));
		Assert.Equal(InstallState.Installed, game.LocalAppState.InstallStatus);
		Assert.Equal(InstallState.NotInstalled, storage.LocalAppStateDictionary["dlc"].InstallStatus);
		Assert.True(installer.CanManageContent(game.AppName));
	}

	[Fact]
	public async Task NewlySelectedTagAndDlcAreInstalledThroughQueueInExistingFolder()
	{
		var (storage, library, installer, game) = await CreateContentInstallation();
		var location = game.LocalAppState!.InstallPath;
		game.LocalAppState.SelectedInstallTags = ["en"];
		File.Delete(Path.Combine(location, "hd.bin"));
		storage.LocalAppStateDictionary["dlc"].InstallStatus = InstallState.NotInstalled;
		File.Delete(Path.Combine(location, "dlc.bin"));
		var completed = WaitForAction(installer, "dlc");
		Assert.True(installer.TryQueueContentChange(game.AppName, ["en", "hd"], ["dlc"]));
		await completed;

		Assert.True(File.Exists(Path.Combine(location, "hd.bin")));
		Assert.True(File.Exists(Path.Combine(location, "dlc.bin")));
		Assert.Equal(location, storage.LocalAppStateDictionary["dlc"].InstallPath);
		Assert.Equal(InstallState.Installed, storage.LocalAppStateDictionary["dlc"].InstallStatus);
		Assert.Equal(["en", "hd"], game.LocalAppState.SelectedInstallTags);
	}

	[Fact]
	public async Task UnknownTagsFailWithoutRemovingFilesOrChangingSelection()
	{
		var (storage, library, installer, game) = await CreateContentInstallation();
		var completed = WaitForAction(installer, game.AppName, ActionStatus.Failed);
		Assert.True(installer.TryQueueContentChange(game.AppName, ["unknown"], []));
		await completed;
		Assert.Null(game.LocalAppState!.SelectedInstallTags);
		Assert.True(File.Exists(Path.Combine(game.LocalAppState.InstallPath, "hd.bin")));
		Assert.Equal(InstallState.Installed, storage.LocalAppStateDictionary["dlc"].InstallStatus);
		Assert.Empty(installer.GetQueueItemNames());
	}

	[Fact]
	public async Task UnsafeManifestPathFailsBeforeAnyRemoval()
	{
		var (storage, library, installer, game) = await CreateContentInstallation();
		await storage.CacheManifestBytes(game.AppName, "1.0", CreateContentManifest(("base.exe", []), ("hd.bin", ["hd"]), ("../escape.bin", ["hd"])));
		var completed = WaitForAction(installer, game.AppName, ActionStatus.Failed);
		Assert.True(installer.TryQueueContentChange(game.AppName, [], ["dlc"]));
		await completed;
		Assert.True(File.Exists(Path.Combine(game.LocalAppState!.InstallPath, "hd.bin")));
		Assert.Null(game.LocalAppState.SelectedInstallTags);
	}

	[Fact]
	public async Task DlcActionInProgressDisablesBaseContentManagement()
	{
		var (storage, library, installer, game) = await CreateContentInstallation();
		var completed = WaitForAction(installer, "dlc");
		installer.AddToQueue(new InstallItem("dlc", ActionType.Repair, game.LocalAppState!.InstallPath));
		Assert.False(installer.CanManageContent(game.AppName));
		Assert.False(installer.TryQueueContentChange(game.AppName, [], []));
		await completed;
	}

	[Fact]
	public async Task ManageDialogRecalculatesDownloadAndSignedDiskDeltaWhenSelectionChanges()
	{
		var (storage, library, installer, game) = await CreateContentInstallation();
		var manifest = Encoding.UTF8.GetString(CreateJsonManifest(100, 10))
			.Replace("\"FileHash\":", "\"InstallTags\":[\"hd\"],\"FileHash\":");
		await storage.CacheManifestBytes(game.AppName, "1.0", Encoding.UTF8.GetBytes(manifest));
		var vm = new AppInstallDialogViewModel(_logger, installer, library, storage, new ImmediateUiDispatcher());
		await vm.InitializeAsync(game);
		vm.AvailableInstallTags.Single().IsSelected = false;
		Assert.Equal("0 B", vm.TotalDownloadSize);
		Assert.Equal("-10 B", vm.TotalInstallSize);

		game.LocalAppState!.SelectedInstallTags = [];
		await vm.InitializeAsync(game);
		vm.AvailableInstallTags.Single().IsSelected = true;
		Assert.Equal("100 B", vm.TotalDownloadSize);
		Assert.Equal("10 B", vm.TotalInstallSize);
	}

	[Fact]
	public async Task MissingInstalledManifestCannotUseDifferentVersionsRemovalList()
	{
		var (storage, library, installer, game) = await CreateContentInstallation();
		game.LocalAppState!.Version = "unavailable-old-version";
		var completed = WaitForAction(installer, game.AppName, ActionStatus.Failed);
		Assert.True(installer.TryQueueContentChange(game.AppName, [], []));
		await completed;
		Assert.True(File.Exists(Path.Combine(game.LocalAppState.InstallPath, "hd.bin")));
		Assert.Null(game.LocalAppState.SelectedInstallTags);
		Assert.Equal(InstallState.Installed, storage.LocalAppStateDictionary["dlc"].InstallStatus);
	}

	[Fact]
	public async Task ContentChangeUsesInstalledVersionAndPreservesPendingUpdate()
	{
		var (storage, library, installer, game) = await CreateContentInstallation();
		game.AssetInfos.Windows.BuildVersion = "2.0";
		game.LocalAppState!.InstallStatus = InstallState.NeedUpdate;
		var completed = WaitForAction(installer, game.AppName);
		Assert.True(installer.CanManageContent(game.AppName));
		Assert.True(installer.TryQueueContentChange(game.AppName, ["en"], ["dlc"]));
		await completed;
		Assert.Equal(InstallState.NeedUpdate, game.LocalAppState.InstallStatus);
		Assert.Equal("1.0", game.LocalAppState.Version);
		Assert.Equal(["en"], game.LocalAppState.SelectedInstallTags);
		Assert.False(File.Exists(Path.Combine(game.LocalAppState.InstallPath, "hd.bin")));
	}

	[Fact]
	public async Task UnknownDlcCannotEnterTheContentQueue()
	{
		var (storage, library, installer, game) = await CreateContentInstallation();
		Assert.Throws<ArgumentException>(() => installer.TryQueueContentChange(game.AppName, [], ["another-games-dlc"]));
		Assert.Null(installer.CurrentInstall);
		Assert.Empty(installer.GetQueueItemNames());
	}

	private async Task<(Storage Storage, LibraryManager Library, InstallManager Installer, Game Game)> CreateContentInstallation()
	{
		var repository = new ControlledManifestRepository();
		var storage = new Storage(_logger, _root, Path.Combine(_root, "games"));
		var location = Path.Combine(_root, "existing-game");
		Directory.CreateDirectory(location);
		var game = CreateGame("game", "Game", "image");
		var dlc = CreateGame("dlc", "DLC", "image");
		dlc.Metadata.MainGameItem = new MainGameItem { Id = game.Metadata.Id };
		var baseManifest = CreateContentManifest(("base.exe", []), ("en.bin", ["en"]), ("hd.bin", ["hd"]), ("shared.bin", []));
		var dlcManifest = CreateContentManifest(("dlc.bin", []), ("shared.bin", []));
		foreach (var (app, manifest) in new[] { (game, baseManifest), (dlc, dlcManifest) })
		{
			app.BaseUrls = [];
			app.LocalAppState = new LocalAppState { AppName = app.AppName, InstallPath = location, Version = "1.0", CachedManifestVersion = "1.0", InstallStatus = InstallState.Installed };
			storage.SaveMetaData(app);
			storage.AddToLocalAppState(app.AppName, app.LocalAppState);
			await storage.CacheManifestBytes(app.AppName, "1.0", manifest);
			repository.Complete(app.AppName, manifest);
			foreach (var file in Manifest.ReadAll(manifest).FileManifestList.Elements)
				File.WriteAllBytes(Path.Combine(location, file.Filename), []);
		}
		var library = CreateLibrary(storage, repository);
		var installer = new InstallManager(_logger, library, repository, storage,
			new DownloadManager(_logger, new HttpClient()), new UnusedShortcutManager(), new AllowInstallPermissionChecker());
		return (storage, library, installer, game);
	}

	private static async Task WaitForAction(InstallManager installer, string appName, ActionStatus expected = ActionStatus.Success)
	{
		var completion = new TaskCompletionSource<InstallItem>(TaskCreationOptions.RunContinuationsAsynchronously);
		void OnStatus(InstallItem item)
		{
			if (item.AppName == appName && item.Status is ActionStatus.Success or ActionStatus.Failed or ActionStatus.Cancelled)
				completion.TrySetResult(item);
		}
		installer.InstallationStatusChanged += OnStatus;
		try
		{
			var item = await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
			Assert.True(item.Status == expected, item.StatusMessage);
		}
		finally
		{
			installer.InstallationStatusChanged -= OnStatus;
		}
	}

	private static byte[] CreateContentManifest(params (string Filename, string[] Tags)[] files) =>
		Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new
		{
			BuildVersionString = "1.0",
			LaunchExeString = "base.exe",
			FileManifestList = files.Select(file => new
			{
				file.Filename,
				InstallTags = file.Tags,
				FileHash = EncodeDecimalBytes(System.Security.Cryptography.SHA1.HashData([])),
				FileChunkParts = Array.Empty<object>()
			}),
			ChunkHashList = new { }, ChunkShaList = new { }, DataGroupList = new { }, ChunkFilesizeList = new { }
		}));

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
