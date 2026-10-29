using System;
using System.Globalization;
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
	public bool IsVerifying { get; init; }
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
			// Events can come from games that were pushed into the paused list; the summary
			// always mirrors the install that currently owns the pipeline.
			var shown = _installManager.CurrentInstall
				?? (installItem is not null && installItem.Status is not ActionStatus.Paused ? installItem : null);

			if (shown == null)
			{
				DispatcherQueue.TryEnqueue(() =>
				{
					EmptyDownloadText.Visibility = Visibility.Visible;
					DownloadStatus.Visibility = Visibility.Collapsed;
					SummaryChanged?.Invoke(new DownloadSummary());
				});
				return;
			}

			var title = _libraryManager.GetGameInfo(shown.AppName)?.AppTitle ?? shown.AppName;

			DispatcherQueue.TryEnqueue(() =>
			{
				UpdateStatus(shown, title);
				RaiseSummary(shown, title);
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

		if (installItem.IsVerifying)
		{
			// Sizes and progress are unknown until the disk check finishes.
			DownloadedSize.Text = "Checking files…";
			return;
		}

		switch (installItem.Status)
		{
			case ActionStatus.Processing:
				ProgressBar.IsIndeterminate = false;
				ProgressBar.Value = installItem.ProgressPercentage;
				DownloadedSize.Text =
					FormatProgress(installItem);
				DownloadSpeed.Text = StorageSizeFormatter.FormatSpeed(installItem.DownloadSpeedRawMiB);
				break;
			case ActionStatus.Paused:
				ProgressBar.IsIndeterminate = false;
				ProgressBar.Value = installItem.ProgressPercentage;
				DownloadedSize.Text =
					FormatProgress(installItem);
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

			switch (installItem.Status)
			{
				case ActionStatus.Processing:
					DispatcherQueue.TryEnqueue(() =>
					{
						ProgressBar.Value = installItem.ProgressPercentage;
						DownloadedSize.Text =
							FormatProgress(installItem);
						DownloadSpeed.Text = StorageSizeFormatter.FormatSpeed(installItem.DownloadSpeedRawMiB);
						RaiseSummary(installItem, GameName.Text);
					});
					break;
				case ActionStatus.Pending:
				case ActionStatus.Cancelling:
					DispatcherQueue.TryEnqueue(() =>
					{
						ProgressBar.IsIndeterminate = true;
						RaiseSummary(installItem, GameName.Text);
					});
					break;
			}
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

		if (installItem.IsVerifying)
		{
			SummaryChanged?.Invoke(new DownloadSummary
			{
				HasActiveDownload = true,
				IsVerifying = true,
				ToolTip = $"{title} - checking files"
			});
			return;
		}

		var summary = installItem.Status switch
		{
			ActionStatus.Pending => new DownloadSummary
			{
				HasActiveDownload = true,
				ProgressPercentage = percentage,
				ToolTip = $"{title} - {label.ToLowerInvariant()} pending"
			},
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
			ActionStatus.Cancelling => new DownloadSummary
			{
				HasActiveDownload = true,
				ProgressPercentage = percentage,
				ToolTip = $"{title} - {label.ToLowerInvariant()} cancelling"
			},
			ActionStatus.Success => new DownloadSummary { ToolTip = $"{title} - {label.ToLowerInvariant()} completed" },
			ActionStatus.Failed => new DownloadSummary { ToolTip = $"{title} - {label.ToLowerInvariant()} failed" },
			ActionStatus.Cancelled => new DownloadSummary { ToolTip = $"{title} - {label.ToLowerInvariant()} cancelled" },
			_ => new DownloadSummary { ToolTip = title }
		};

		SummaryChanged?.Invoke(summary);
	}

	// The pane is narrow: one decimal and a single unit keep size and speed on one line.
	private static string FormatProgress(InstallItem installItem)
	{
		var written = installItem.WrittenSizeMiB;
		var total = installItem.TotalWriteSizeMb;
		return total >= 1024
			? string.Create(CultureInfo.InvariantCulture, $"{written / 1024:F1} / {total / 1024:F1} GB")
			: string.Create(CultureInfo.InvariantCulture, $"{written:F1} / {total:F1} MB");
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
