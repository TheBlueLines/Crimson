using System.Reflection;
using Crimson.Core;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Crimson.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;

namespace Crimson.Tests;

public sealed class GameInfoViewModelTests : IDisposable
{
	private const string AppName = "test";

	private readonly string _root = Path.Combine(Path.GetTempPath(), $"crimson-gameinfo-{Guid.NewGuid():N}");
	private readonly HttpClient _http = new();
	private readonly Serilog.Core.Logger _logger = new LoggerConfiguration().CreateLogger();
	private readonly InstallManager _installer;
	private readonly GameInfoViewModel _viewModel;

	public GameInfoViewModelTests()
	{
		Directory.CreateDirectory(_root);
		var storage = new Storage(_logger, Path.Combine(_root, "data"), _root);
		var game = new Game
		{
			AppName = AppName,
			AppTitle = "Test Game",
			AssetInfos = new AssetInfos
			{
				Windows = new Asset { AppName = AppName, Namespace = AppName, CatalogItemId = AppName, BuildVersion = "1.0" }
			},
			Metadata = new Metadata { Id = AppName, KeyImages = [] },
			LocalAppState = new LocalAppState { AppName = AppName, InstallStatus = InstallState.NotInstalled }
		};
		storage.SaveMetaData(game);
		storage.AddToLocalAppState(AppName, game.LocalAppState);

		var repository = new StubStoreRepository();
		var auth = new AuthManager(_logger, storage, new PassthroughCredentialProtector(), _http);
		var library = new LibraryManager(_logger, repository, storage, auth);
		_installer = new InstallManager(
			_logger,
			library,
			repository,
			storage,
			new DownloadManager(_logger, _http),
			null!,
			new AllowInstallPermissionChecker());
		var settings = new SettingsManager(storage, NullLogger<SettingsManager>.Instance, _root, _root);
		var cloudSaves = new CloudSaveManager(
			_logger,
			null!,
			library,
			settings,
			() => Task.FromResult<string?>(null),
			Path.Combine(_root, "cloud-saves"));
		_viewModel = new GameInfoViewModel(
			NullLogger<GameInfoViewModel>.Instance,
			_installer,
			library,
			storage,
			new ImmediateUiDispatcher(),
			cloudSaves);
	}

	[Fact]
	public async Task OpeningThePageWithoutAnInstallOffersInstall()
	{
		await _viewModel.OnNavigatedTo(AppName);

		Assert.Equal("Install", _viewModel.PrimaryActionButtonText);
		Assert.False(_viewModel.IsProgressRingVisible);
	}

	[Fact]
	public async Task OpeningThePageDuringAnInstallShowsItsProgress()
	{
		SetCurrentInstall(new InstallItem(AppName, ActionType.Install, _root)
		{
			Status = ActionStatus.Processing,
			ProgressPercentage = 42
		});

		await _viewModel.OnNavigatedTo(AppName);

		Assert.Equal("42%", _viewModel.PrimaryActionButtonText);
		Assert.True(_viewModel.IsProgressRingVisible);
		Assert.False(_viewModel.IsPrimaryActionEnabled);
		Assert.Equal(42, _viewModel.ProgressValue);
	}

	[Fact]
	public async Task OpeningThePageForAnInstallPausedByAnotherDownloadShowsPaused()
	{
		var suspended = (List<InstallItem>)typeof(InstallManager)
			.GetField("_suspendedInstalls", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(_installer)!;
		suspended.Add(new InstallItem(AppName, ActionType.Install, _root)
		{
			Status = ActionStatus.Paused,
			ProgressPercentage = 10
		});

		await _viewModel.OnNavigatedTo(AppName);

		Assert.Equal("Paused", _viewModel.PrimaryActionButtonText);
		Assert.True(_viewModel.IsProgressRingVisible);
	}

	[Fact]
	public async Task AnotherGamesInstallDoesNotAffectThePage()
	{
		SetCurrentInstall(new InstallItem("other", ActionType.Install, _root)
		{
			Status = ActionStatus.Processing,
			ProgressPercentage = 42
		});

		await _viewModel.OnNavigatedTo(AppName);

		Assert.Equal("Install", _viewModel.PrimaryActionButtonText);
		Assert.False(_viewModel.IsProgressRingVisible);
	}

	public void Dispose()
	{
		_viewModel.Cleanup();
		_http.Dispose();
		_logger.Dispose();
		if (Directory.Exists(_root))
			Directory.Delete(_root, recursive: true);
	}

	private void SetCurrentInstall(InstallItem install) =>
		typeof(InstallManager)
			.GetProperty(nameof(InstallManager.CurrentInstall))!
			.SetValue(_installer, install);

	private sealed class ImmediateUiDispatcher : IUiDispatcher
	{
		public bool TryEnqueue(Action callback)
		{
			callback();
			return true;
		}
	}

	private sealed class PassthroughCredentialProtector : ICredentialProtector
	{
		public string Protect(string value) => value;
		public string Unprotect(string protectedValue) => protectedValue;
	}

	private sealed class AllowInstallPermissionChecker : IInstallPermissionChecker
	{
		public InstallPermissionCheckResult Check(string folderPath) => new(true);
	}

	private sealed class StubStoreRepository : IStoreRepository
	{
		public Task<GetManifestUrlData> GetManifestUrls(string nameSpace, string catalogItem, string appName, string platform = "Windows", string label = "Live", CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<byte[]> GetGameManifest(GetManifestUrlData data, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<Metadata> FetchGameMetaData(string nameSpace, string catalogItemId) => throw new NotSupportedException();
		public Task<IEnumerable<Asset>> FetchGameAssets(string platform = "Windows", string label = "Live") => throw new NotSupportedException();
		public Task DownloadFileAsync(string url, string destinationPath) => throw new NotSupportedException();
		public Task<string> GetGameToken() => throw new NotSupportedException();
		public Task<byte[]?> GetOwnershipToken(string nameSpace, string catalogItemId) => throw new NotSupportedException();
	}
}
