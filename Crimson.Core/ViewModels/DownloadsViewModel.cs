using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crimson.Core;
using Crimson.Models;
using Crimson.Utils;
using Serilog;

namespace Crimson.ViewModels;

public partial class DownloadsViewModel : ObservableObject, IDisposable
{

	private readonly ILogger _log;
	private readonly InstallManager _installManager;
	private readonly LibraryManager _libraryManager;
	private readonly IUiDispatcher _uiDispatcher;

	[ObservableProperty]
	private DownloadManagerItem _currentInstallItem = new DownloadManagerItem();

	[ObservableProperty]
	private ObservableCollection<DownloadManagerItem> _queueItems = new();

	[ObservableProperty]
	private ObservableCollection<PausedDownloadItem> _pausedItems = new();

	[ObservableProperty]
	private bool _hasPausedItems = false;

	partial void OnPausedItemsChanged(ObservableCollection<PausedDownloadItem> value) =>
		HasPausedItems = value is { Count: > 0 };

	[ObservableProperty]
	private ObservableCollection<DownloadManagerItem> _historyItems = new();

	[ObservableProperty]
	private bool _showPauseButton = false;

	[ObservableProperty]
	private bool _showResumeButton = false;

	[ObservableProperty]
	private bool _enablePauseButton = false;

	[ObservableProperty]
	private bool _showCurrentDownload = false;

	[ObservableProperty]
	private bool _downloadProgressBarIndeterminate = false;

	[ObservableProperty]
	private double _downloadProgressBarValue;

	[ObservableProperty]
	private string _currentInstallItemName = string.Empty;

	[ObservableProperty]
	private string? _currentInstallItemImageUrl;

	[ObservableProperty]
	private string _currentInstallAction = string.Empty;

	[ObservableProperty]
	private string _currentDownloadSpeed = string.Empty;

	[ObservableProperty]
	private string _currentDownloadSize = string.Empty;

	public DownloadsViewModel(
		ILogger logger,
		InstallManager installManager,
		LibraryManager libraryManager,
		IUiDispatcher uiDispatcher)
	{
		_log = logger;
		_uiDispatcher = uiDispatcher;
		_log.Information("DownloadsPage: Loading Page");

		_installManager = installManager;
		_libraryManager = libraryManager;

		var gameInQueue = _installManager.CurrentInstall;
		HandleInstallationStatusChanged(gameInQueue);
		FetchQueueItemsList();
		FetchPausedItemsList();
		FetchHistoryItemsList();
		_installManager.InstallationStatusChanged += HandleInstallationStatusChanged;
		_installManager.InstallProgressUpdate += InstallationProgressUpdate;
	}

	private void FetchQueueItemsList()
	{
		try
		{
			QueueItems.Clear();
			var queueItemNames = _installManager.GetQueueItemNames();
			if (queueItemNames == null || queueItemNames.Count < 1) return;

			ObservableCollection<DownloadManagerItem> itemList = new();
			foreach (var queueItemName in queueItemNames)
			{

				var gameInfo = _libraryManager.GetGameInfo(queueItemName);
				if (gameInfo is null) continue;
				itemList.Add(new DownloadManagerItem()
				{
					Name = queueItemName,
					Title = gameInfo.AppTitle,
					ImageUrl = gameInfo.Metadata.KeyImages
						.FirstOrDefault(image => image.Type == "DieselGameBoxTall")?.Url
				});

			}
			QueueItems = itemList;
		}
		catch (Exception ex)
		{
			_log.Error(ex, "FetchQueueItemsList: Exception occurred");
		}
	}
	private void FetchPausedItemsList()
	{
		try
		{
			ObservableCollection<PausedDownloadItem> itemList = new();
			foreach (var install in _installManager.SuspendedInstalls)
			{
				var gameInfo = _libraryManager.GetGameInfo(install.AppName);
				if (gameInfo is null) continue;
				itemList.Add(new PausedDownloadItem()
				{
					Name = install.AppName,
					Title = gameInfo.AppTitle,
					ImageUrl = gameInfo.Metadata.KeyImages
						.FirstOrDefault(image => image.Type == "DieselGameBoxTall")?.Url,
					ProgressPercentage = Math.Clamp(install.ProgressPercentage, 0, 100),
					SizeText = FormatProgressSize(install)
				});
			}
			PausedItems = itemList;
		}
		catch (Exception ex)
		{
			_log.Error(ex, "FetchPausedItemsList: Exception occurred");
		}
	}

	private void FetchHistoryItemsList()
	{
		try
		{
			var historyItemsNames = _installManager.GetHistoryItemsNames();
			if (historyItemsNames == null || historyItemsNames.Count < 1) return;

			_log.Debug("FetchHistoryItemsList: Loaded {Count} history items", historyItemsNames.Count);
			HistoryItems.Clear();

			ObservableCollection<DownloadManagerItem> itemList = new();

			foreach (var historyItemName in historyItemsNames)
			{

				var gameInfo = _libraryManager.GetGameInfo(historyItemName);
				if (gameInfo is null) continue;
				itemList.Add(new DownloadManagerItem()
				{
					Name = historyItemName,
					Title = gameInfo.AppTitle,
					ImageUrl = gameInfo.Metadata.KeyImages
						.FirstOrDefault(image => image.Type == "DieselGameBoxTall")?.Url
				});
			}
			HistoryItems = itemList;
		}
		catch (Exception ex)
		{
			_log.Error(ex, "FetchHistoryItemsList: Error while fetching history items");
		}
	}


	private void HandleInstallationStatusChanged(InstallItem? installItem)
	{
		try
		{
			UpdateUIProperties(() => RefreshAndApplyStatus(installItem));
		}
		catch (Exception ex)
		{
			_log.Error(ex, "Exception in HandleInstallationStatusChanged");
		}
	}

	private void RefreshAndApplyStatus(InstallItem? installItem)
	{
		_log.Information("HandleInstallationStatusChanged: Handling Installation Status Change");
		FetchQueueItemsList();
		FetchHistoryItemsList();
		FetchPausedItemsList();

		// Events from games pushed into the paused list only refresh that list; the active slot
		// always mirrors whatever install currently owns the download pipeline.
		var current = _installManager.CurrentInstall;
		if (current == null && installItem?.Status == ActionStatus.Cancelling)
			current = installItem;

		if (current == null)
		{
			_log.Information("HandleInstallationStatusChanged: No installation in progress");
			ShowCurrentDownload = false;
			return;
		}

		ShowCurrentDownload = true;
		DownloadProgressBarIndeterminate = true;
		SetCurrentInstallItem(current);
		ApplyInstallationStatus(current);
		UpdatePauseResumeControls(current.Status);
	}

	private void SetCurrentInstallItem(InstallItem installItem)
	{
		var gameInfo = _libraryManager.GetGameInfo(installItem.AppName);
		_log.Debug("HandleInstallationStatusChanged: Loaded {AppName}", gameInfo.AppName);
		CurrentInstallItem = new DownloadManagerItem
		{
			Name = gameInfo.AppName,
			Title = gameInfo.AppTitle,
			InstallState = gameInfo.LocalAppState?.InstallStatus ?? InstallState.NotInstalled,
			ImageUrl = gameInfo.Metadata.KeyImages
				.FirstOrDefault(image => image.Type == "DieselGameBoxTall")?.Url
		};
		CurrentInstallItemName = CurrentInstallItem.Title;
		CurrentInstallItemImageUrl = CurrentInstallItem.ImageUrl;
	}

	private void ApplyInstallationStatus(InstallItem installItem)
	{
		_log.Information("HandleInstallationStatusChanged: Installation Status: {Status}", installItem.Status);
		switch (installItem.Status)
		{
			case ActionStatus.Processing:
				ApplyProcessingState(installItem);
				break;
			case ActionStatus.Paused:
				ApplyPausedState(installItem);
				break;
			case ActionStatus.Cancelling:
				ApplyCancellingState();
				break;
			case ActionStatus.Success:
			case ActionStatus.Failed:
			case ActionStatus.Cancelled:
				ShowCurrentDownload = false;
				break;
		}
	}

	private void ApplyProcessingState(InstallItem installItem)
	{
		DownloadProgressBarIndeterminate = false;
		DownloadProgressBarValue = Convert.ToDouble(installItem.ProgressPercentage);
		CurrentInstallAction = $@"{installItem.Action}ing";
		CurrentDownloadSize = FormatProgressSize(installItem);
		CurrentDownloadSpeed = StorageSizeFormatter.FormatSpeed(installItem.DownloadSpeedRawMiB);
	}

	private void ApplyPausedState(InstallItem installItem)
	{
		if (installItem.IsVerifying)
		{
			// Sizes and progress are unknown until the disk check finishes.
			DownloadProgressBarIndeterminate = true;
			CurrentInstallAction = "Checking files";
			CurrentDownloadSize = string.Empty;
			CurrentDownloadSpeed = string.Empty;
			return;
		}

		DownloadProgressBarIndeterminate = false;
		DownloadProgressBarValue = Convert.ToDouble(installItem.ProgressPercentage);
		CurrentInstallAction = "Paused";
		CurrentDownloadSize = FormatProgressSize(installItem);
		CurrentDownloadSpeed = string.Empty;
	}

	private void ApplyCancellingState()
	{
		DownloadProgressBarIndeterminate = true;
		CurrentInstallAction = "Cancelling";
		CurrentDownloadSize = string.Empty;
		CurrentDownloadSpeed = string.Empty;
	}

	private static string FormatProgressSize(InstallItem installItem) =>
		$@"{StorageSizeFormatter.FormatMebibytes(installItem.WrittenSizeMiB)} of {StorageSizeFormatter.FormatMebibytes(installItem.TotalWriteSizeMb)}";

	private void UpdatePauseResumeControls(ActionStatus status)
	{
		if (status == ActionStatus.Paused)
		{
			ShowResumeButton = true;
			ShowPauseButton = false;
			return;
		}

		ShowResumeButton = false;
		ShowPauseButton = true;
		EnablePauseButton = status == ActionStatus.Processing;
	}

	private void InstallationProgressUpdate(InstallItem installItem)
	{
		// Its better not to log the progress update as it will be called very frequently
		// It can make the log file very big
		try
		{

			if (installItem == null) return;

			if (installItem.Status != ActionStatus.Processing) return;
			_ = UpdateUIProperties(() =>
			{
				DownloadProgressBarIndeterminate = false;
				DownloadProgressBarValue = Convert.ToDouble(installItem.ProgressPercentage);
				CurrentDownloadSize = $@"{StorageSizeFormatter.FormatMebibytes(installItem.WrittenSizeMiB)} of {StorageSizeFormatter.FormatMebibytes(installItem.TotalWriteSizeMb)}";
				CurrentDownloadSpeed = StorageSizeFormatter.FormatSpeed(installItem.DownloadSpeedRawMiB);
			});
		}
		catch (Exception ex)
		{
			_log.Error(ex, "InstallationProgressUpdate: Error while updating progress");
		}
	}

	[RelayCommand]
	private void CancelInstall()
	{
		_log.Information("CancelInstallButton_OnClick: Cancelling Installation");
		_installManager.CancelInstall(CurrentInstallItem.Name);
	}

	[RelayCommand]
	private void PauseInstall()
	{
		Task.Run(() => _installManager.PauseInstall());
	}

	[RelayCommand]
	private void ResumeInstall()
	{
		Task.Run(() => _installManager.ResumeInstall());
	}

	public void ResumePausedGame(string appName)
	{
		if (string.IsNullOrEmpty(appName)) return;
		_log.Information("ResumePausedGame: Resuming paused install {AppName}", appName);
		_ = Task.Run(() => _installManager.ResumeSuspendedInstallAsync(appName));
	}

	public void CancelPausedGame(string appName)
	{
		if (string.IsNullOrEmpty(appName)) return;
		_log.Information("CancelPausedGame: Cancelling paused install {AppName}", appName);
		_installManager.CancelSuspendedInstall(appName);
	}

	private bool UpdateUIProperties(Action updateAction)
	{
		return _uiDispatcher.TryEnqueue(updateAction);

	}
	public void Dispose()
	{
		_installManager.InstallationStatusChanged -= HandleInstallationStatusChanged;
		_installManager.InstallProgressUpdate -= InstallationProgressUpdate;
	}

}


