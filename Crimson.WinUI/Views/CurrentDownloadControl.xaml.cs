using System;
using Crimson.Core;
using Crimson.Models;
using Crimson.Utils;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace Crimson.Views;

public sealed class DownloadSummary
{
	public bool HasActiveDownload { get; init; }
	public int ProgressPercentage { get; init; }
	public string ToolTip { get; init; } = "No downloads in queue";
}

public sealed partial class CurrentDownloadControl : UserControl
{
	public event Action<DownloadSummary>? SummaryChanged;

	private readonly ILogger _log;
	private readonly LibraryManager _libraryManager;
	private readonly InstallManager _installManager;
	public CurrentDownloadControl()
	{
		InitializeComponent();
		_log = App.GetService<ILogger>();
		_installManager = App.GetService<InstallManager>();
		_libraryManager = App.GetService<LibraryManager>();

		var gameInQueue = _installManager.CurrentInstall;
		HandleInstallationStatusChanged(gameInQueue);
		_installManager.InstallationStatusChanged += HandleInstallationStatusChanged;
		_installManager.InstallProgressUpdate += InstallationProgressUpdate;

	}

	public void PublishCurrentSummary() => HandleInstallationStatusChanged(_installManager.CurrentInstall);

	// Handing Installtion State Change
	// This function is never run on UI Thread
	// So always make sure to use Dispatcher Queue to update UI thread
	private void HandleInstallationStatusChanged(InstallItem? installItem)
	{
		try
		{
			if (installItem == null)
			{
				DispatcherQueue.TryEnqueue(() =>
				{
					EmptyDownloadText.Visibility = Visibility.Visible;
					DownloadStatus.Visibility = Visibility.Collapsed;
					SummaryChanged?.Invoke(new DownloadSummary());
				});
				return;
			}

			var gameInfo = _libraryManager.GetGameInfo(installItem.AppName);
			if (gameInfo == null) return;

			DispatcherQueue.TryEnqueue(() =>
			{
				UpdateStatus(installItem, gameInfo.AppTitle);
				RaiseSummary(installItem, gameInfo.AppTitle);
			});
		}
		catch (Exception ex)
		{
			_log.Error(ex, "Failed to update download status");
		}
	}

	private void UpdateStatus(InstallItem installItem, string Title)
	{
		DownloadSpeed.Text = "";
		DownloadedSize.Text = "";
		ProgressBar.IsEnabled = true;
		ProgressBar.IsIndeterminate = true;
		EmptyDownloadText.Visibility = Visibility.Collapsed;
		DownloadStatus.Visibility = Visibility.Visible;
		GameName.Text = Title;

		switch (installItem.Status)
		{
			case ActionStatus.Processing:
				ProgressBar.IsIndeterminate = false;
				ProgressBar.Value = installItem.ProgressPercentage;
				DownloadedSize.Text =
					$@"{StorageSizeFormatter.FormatMebibytes(installItem.WrittenSizeMiB)} of {StorageSizeFormatter.FormatMebibytes(installItem.TotalWriteSizeMb)}";
				DownloadSpeed.Text = $@"{installItem.DownloadSpeedRawMiB} MB/s";
				break;
			case ActionStatus.Paused:
				ProgressBar.IsIndeterminate = false;
				ProgressBar.Value = installItem.ProgressPercentage;
				DownloadedSize.Text =
					$@"{StorageSizeFormatter.FormatMebibytes(installItem.WrittenSizeMiB)} of {StorageSizeFormatter.FormatMebibytes(installItem.TotalWriteSizeMb)}";
				DownloadSpeed.Text = "Paused";
				break;
			case ActionStatus.Success:
				DownloadedSize.Text = GetActionLabel(installItem.Action) + " Completed";
				DownloadSpeed.Text = installItem.StatusMessage ?? string.Empty;
				ProgressBar.IsIndeterminate = false;
				ProgressBar.Value = 100;
				break;
			case ActionStatus.Failed:
				DownloadedSize.Text = GetActionLabel(installItem.Action) + " Failed";
				DownloadSpeed.Text = installItem.StatusMessage ?? string.Empty;
				ProgressBar.IsIndeterminate = false;
				ProgressBar.Value = 100;
				break;
			case ActionStatus.Cancelled:
				DownloadedSize.Text = GetActionLabel(installItem.Action) + " Cancelled";
				DownloadSpeed.Text = string.Empty;
				ProgressBar.IsIndeterminate = false;
				ProgressBar.Value = 100;
				break;
		}
	}

	private void InstallationProgressUpdate(InstallItem? installItem)
	{
		try
		{
			if (installItem == null) return;

			if (installItem.Status != ActionStatus.Processing) return;
			DispatcherQueue.TryEnqueue(() =>
			{
				ProgressBar.Value = installItem.ProgressPercentage;
				DownloadedSize.Text =
					$@"{StorageSizeFormatter.FormatMebibytes(installItem.WrittenSizeMiB)} of {StorageSizeFormatter.FormatMebibytes(installItem.TotalWriteSizeMb)}";
				DownloadSpeed.Text = $@"{installItem.DownloadSpeedRawMiB} MB/s";
				RaiseSummary(installItem, GameName.Text);
			});
		}
		catch (Exception ex)
		{
			_log.Error(ex, "Failed to update download progress");
		}
	}

	private void RaiseSummary(InstallItem installItem, string title)
	{
		var percentage = Math.Clamp(installItem.ProgressPercentage, 0, 100);
		var label = GetActionLabel(installItem.Action);

		var summary = installItem.Status switch
		{
			ActionStatus.Processing => new DownloadSummary
			{
				HasActiveDownload = true,
				ProgressPercentage = percentage,
				ToolTip = $"{title} - {percentage}%"
			},
			ActionStatus.Paused => new DownloadSummary
			{
				HasActiveDownload = true,
				ProgressPercentage = percentage,
				ToolTip = $"{title} - paused at {percentage}%"
			},
			ActionStatus.Success => new DownloadSummary { ToolTip = $"{title} - {label.ToLowerInvariant()} completed" },
			ActionStatus.Failed => new DownloadSummary { ToolTip = $"{title} - {label.ToLowerInvariant()} failed" },
			ActionStatus.Cancelled => new DownloadSummary { ToolTip = $"{title} - {label.ToLowerInvariant()} cancelled" },
			_ => new DownloadSummary { ToolTip = title }
		};

		SummaryChanged?.Invoke(summary);
	}

	public static string GetActionLabel(ActionType action) => action switch
	{
		ActionType.Install => "Installation",
		ActionType.Update => "Update",
		ActionType.Repair or ActionType.Verify => "Verification",
		ActionType.Uninstall => "Uninstall",
		ActionType.Move => "Move",
		ActionType.Import => "Import",
		_ => "Operation"
	};
}
