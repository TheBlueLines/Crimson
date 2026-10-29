using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Crimson.Core;
using Crimson.Interfaces;
using Crimson.Models;
using Serilog;

namespace Crimson.ViewModels;

public partial class LibraryViewModel : ObservableObject, INavigationAware
{
	[ObservableProperty]
	private List<LibraryItem> _gamesList;

	[ObservableProperty]
	private bool _loadingFinished = false;

	[ObservableProperty]
	private bool _showLoadingScreen = true;

	[ObservableProperty]
	private bool _showAppGrid = false;

	[ObservableProperty]
	private bool _showQueueItems = false;

	[ObservableProperty]
	private bool _showEmptyState = false;

	private readonly ILogger _log;
	private readonly LibraryManager _libraryManager;
	private readonly InstallManager _installManager;
	private readonly IUiDispatcher _uiDispatcher;
	private int _navigationGeneration;
	private Action<IEnumerable<Game>>? _libraryUpdatedHandler;
	private Action<InstallItem>? _installStatusHandler;
	private Action<InstallItem>? _installProgressHandler;

	public LibraryViewModel(
		ILogger logger,
		LibraryManager libraryManager,
		InstallManager installManager,
		IUiDispatcher uiDispatcher)
	{
		_log = logger;
		_libraryManager = libraryManager;
		_installManager = installManager;
		_uiDispatcher = uiDispatcher;
	}

	public async Task OnNavigatedTo(object parameter)
	{
		OnNavigatedFrom();
		var generation = Volatile.Read(ref _navigationGeneration);
		_libraryUpdatedHandler = games => UpdateLibrary(games, generation);
		_libraryManager.LibraryUpdated += _libraryUpdatedHandler;
		_installStatusHandler = item => UpdateInstallItem(item, resort: true);
		_installProgressHandler = item => UpdateInstallItem(item, resort: false);
		_installManager.InstallationStatusChanged += _installStatusHandler;
		_installManager.InstallProgressUpdate += _installProgressHandler;
		_log.Information("LibraryPage: Loading Page");
		var games = await _libraryManager.GetLibraryData();
		UpdateLibrary(games, generation);
		_log.Information("LibraryPage: Loading finished");
	}

	public void OnNavigatedFrom()
	{
		Interlocked.Increment(ref _navigationGeneration);
		if (_libraryUpdatedHandler is not null)
			_libraryManager.LibraryUpdated -= _libraryUpdatedHandler;
		_libraryUpdatedHandler = null;
		if (_installStatusHandler is not null)
			_installManager.InstallationStatusChanged -= _installStatusHandler;
		_installStatusHandler = null;
		if (_installProgressHandler is not null)
			_installManager.InstallProgressUpdate -= _installProgressHandler;
		_installProgressHandler = null;
	}

	private void UpdateLibrary(IEnumerable<Game> games, int generation)
	{
		try
		{
			_log.Information("UpdateLibrary: Updating Library Page");
			if (games == null || generation != Volatile.Read(ref _navigationGeneration)) return;

			_uiDispatcher.TryEnqueue(() =>
			{
				if (generation != Volatile.Read(ref _navigationGeneration)) return;
				GamesList = new List<LibraryItem>();
				var currentInstall = _installManager.CurrentInstall;
				foreach (var game in games)
				{
					if (game.IsDlc()) continue;
					var item = new LibraryItem
					{
						Name = game.AppName,
						Title = game.AppTitle,
						ImageUrl = game.Metadata.KeyImages
							.FirstOrDefault(image => image.Type == "DieselGameBoxTall")?.Url,
						InstallState = game.LocalAppState?.InstallStatus ?? InstallState.NotInstalled
					};

					if (currentInstall?.AppName == game.AppName && IsActionRunning(currentInstall))
					{
						item.InstallState = MapInstallState(currentInstall);
						item.InstallProgress = GetKnownProgress(currentInstall);
					}

					_log.Debug("UpdateLibrary: Adding {AppName} to library", item.Name);
					GamesList.Add(item);
				}
				GamesList = SortGames(GamesList);
				ShowLoadingScreen = false;
				ShowAppGrid = true;
				ShowEmptyState = GamesList.Count == 0;
			});
			_log.Information("UpdateLibrary: Updated Library Page");
		}
		catch (Exception ex)
		{
			_log.Error(ex, "UpdateLibrary failed");
		}
	}

	private void UpdateInstallItem(InstallItem installItem, bool resort)
	{
		try
		{
			if (installItem is null)
				return;

			_uiDispatcher.TryEnqueue(() =>
			{
				var item = GamesList?.FirstOrDefault(game => game.Name == installItem.AppName);
				if (item is null)
					return;

				item.InstallState = installItem.Status is ActionStatus.Failed or ActionStatus.Cancelled or ActionStatus.Success
					? _libraryManager.GetGameInfo(installItem.AppName)?.LocalAppState?.InstallStatus ?? InstallState.NotInstalled
					: MapInstallState(installItem);
				item.InstallProgress = GetKnownProgress(installItem);

				if (resort && GamesList is not null)
					GamesList = SortGames(GamesList);
			});
		}
		catch (Exception ex)
		{
			_log.Error(ex, "UpdateInstallItem failed");
		}
	}

	private static List<LibraryItem> SortGames(IEnumerable<LibraryItem> games) =>
		games.OrderBy(GetSortGroup)
			.ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
			.ToList();

	private static int GetSortGroup(LibraryItem item) => item.IsInstalled ? 0 : item.IsInstalling ? 1 : 2;

	// A restored install's progress is unknown until its disk check finishes.
	private static int GetKnownProgress(InstallItem installItem) =>
		installItem.IsVerifying ? 0 : installItem.ProgressPercentage;

	private static bool IsActionRunning(InstallItem installItem) =>
		installItem.Status is ActionStatus.Pending or ActionStatus.Processing
			or ActionStatus.Paused or ActionStatus.Cancelling;

	private static InstallState MapInstallState(InstallItem installItem) => installItem.Status switch
	{
		ActionStatus.Pending or ActionStatus.Processing or ActionStatus.Cancelling => InstallState.Installing,
		ActionStatus.Paused => InstallState.InstallationPaused,
		_ => InstallState.NotInstalled
	};
}
