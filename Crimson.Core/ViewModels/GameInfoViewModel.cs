using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crimson.Core;
using Crimson.Interfaces;
using Crimson.Models;
using Crimson.Utils;
using Microsoft.Extensions.Logging;

namespace Crimson.ViewModels;

/// <summary>
/// Page for Showing Details of individual game and allowing play
/// download and other options
/// </summary>
public partial class GameInfoViewModel : ObservableObject, INavigationAware
{
	private readonly IUiDispatcher _uiDispatcher;
	private readonly InstallManager _installer;
	private readonly LibraryManager _libraryManager;
	private readonly Storage _storage;
	private readonly ILogger<GameInfoViewModel> _log;

	[ObservableProperty]
	private Game? _game;

	[ObservableProperty]
	private bool _isInstalled;

	[ObservableProperty]
	private bool _isImportVisible;

	[ObservableProperty]
	private string _primaryActionButtonText;

	[ObservableProperty]
	private GamePrimaryAction _primaryAction;

	[ObservableProperty]
	private bool _isPrimaryActionEnabled = true;

	[ObservableProperty]
	private bool _isProgressRingVisible;

	[ObservableProperty]
	private bool _isProgressRingIndeterminate;

	[ObservableProperty]
	private double _progressValue;

	[ObservableProperty]
	private string? _titleImageUrl;

	[ObservableProperty]
	private bool _isInstallErrorVisible;

	[ObservableProperty]
	private string _installErrorMessage = string.Empty;

	// Event for showing install dialog
	public event Func<Task> ShowInstallDialogRequested;
	public event Action CloseInstallDialogRequested;

	// Event for requesting folder picker from view
	public event Func<Task<string>> FolderPickerRequested;

	public GameInfoViewModel(ILogger<GameInfoViewModel> logger,
			InstallManager installer,
			LibraryManager libraryManager,
			Storage storage,
			IUiDispatcher uiDispatcher)
	{
		_uiDispatcher = uiDispatcher;
		_log = logger;
		_installer = installer;
		_libraryManager = libraryManager;
		_storage = storage;

	}

	public async Task OnNavigatedTo(object parameter)
	{
		if (parameter is not string appName) return;

		Game = _libraryManager.GetGameInfo((string)appName);
		var gameImage = Game.Metadata.KeyImages.FirstOrDefault(image => image.Type == "DieselGameBox");
		TitleImageUrl = gameImage?.Url;

		CheckGameStatus(Game);

		// Unregister event handlers on start
		UnregisterEventHandlers();

		_libraryManager.GameStatusUpdated += CheckGameStatus;
		_installer.InstallationStatusChanged += HandleInstallationStatusChanged;
		_installer.InstallProgressUpdate += HandleInstallationStatusChanged;

	}

	private void UnregisterEventHandlers()
	{
		_libraryManager.GameStatusUpdated -= CheckGameStatus;
		_installer.InstallationStatusChanged -= HandleInstallationStatusChanged;
		_installer.InstallProgressUpdate -= HandleInstallationStatusChanged;
	}

	[RelayCommand]
	private async Task PrimaryActionAsync()
	{
		try
		{
			if (Game == null) return;
			_log.LogInformation("GameInfoPage: Primary Action Button Clicked for {Game}", Game.AppTitle);
			ClearInstallError();

			if (Game.LocalAppState?.InstallStatus == InstallState.Installed)
			{
				_log.LogInformation("GameInfoPage: Starting Game {Game}", Game.AppTitle);
				await _libraryManager.LaunchApp(Game.AppName);
				return;
			}

			if (Game.LocalAppState?.InstallStatus == InstallState.NeedUpdate)
			{
				_log.LogInformation("GameInfoPage: Updating Game {Game}", Game.AppTitle);
				_installer.AddToQueue(new InstallItem(Game.AppName, ActionType.Update, Game.LocalAppState.InstallPath));
				return;
			}

			if (Game.LocalAppState?.InstallStatus == InstallState.Broken)
			{
				_log.LogInformation("GameInfoPage: Repairing Game {Game}", Game.AppTitle);
				_installer.AddToQueue(new InstallItem(Game.AppName, ActionType.Repair, Game.LocalAppState.InstallPath));
				return;
			}

			if (ShowInstallDialogRequested != null)
			{
				await ShowInstallDialogRequested.Invoke();
			}
		}
		catch (Exception ex)
		{
			_log.LogError(ex, "PrimaryActionAsync: Exception");
			IsProgressRingVisible = false;
			IsPrimaryActionEnabled = true;
		}
	}

	/// <summary>
	/// Handing Installation State Change.
	/// <br/>
	/// This function is never run on UI Thread.
	/// <br/>
	/// So always make sure to use Dispatcher Queue to update UI thread
	/// </summary>
	/// <param name="installItem"></param>
	private void HandleInstallationStatusChanged(InstallItem installItem)
	{
		try
		{
			if (installItem == null || installItem.AppName != Game?.AppName) return;
			_uiDispatcher.TryEnqueue(() =>
			{
				_log.LogInformation("GameInfoPage: Installation Status Changed for {Game}", installItem.AppName);
				switch (installItem.Status)
				{
					case ActionStatus.Processing:
						IsProgressRingIndeterminate = false;
						ProgressValue = Convert.ToDouble(installItem.ProgressPercentage);
						IsProgressRingVisible = true;
						IsPrimaryActionEnabled = false;
						PrimaryActionButtonText = $"{installItem.ProgressPercentage}%";
						break;
					case ActionStatus.Pending:
						PrimaryActionButtonText = "Pending...";
						IsProgressRingVisible = true;
						IsProgressRingIndeterminate = true;
						break;

					case ActionStatus.Paused:
						PrimaryActionButtonText = "Paused";
						IsProgressRingVisible = true;
						IsProgressRingIndeterminate = false;
						ProgressValue = Convert.ToDouble(installItem.ProgressPercentage);
						IsPrimaryActionEnabled = true;
						break;

					case ActionStatus.Cancelling:
						PrimaryActionButtonText = "Cancelling...";
						IsProgressRingVisible = true;
						IsProgressRingIndeterminate = true;
						break;

					case ActionStatus.Success:
						IsProgressRingIndeterminate = false;
						ProgressValue = 100;
						IsProgressRingVisible = false;
						IsPrimaryActionEnabled = true;
						ClearInstallError();
						ApplyGameStatus();
						break;

					case ActionStatus.Failed:
						IsProgressRingIndeterminate = false;
						IsProgressRingVisible = false;
						ProgressValue = 0;
						IsPrimaryActionEnabled = true;
						InstallErrorMessage = string.IsNullOrWhiteSpace(installItem.StatusMessage)
							? $"{GetActionLabel(installItem.Action)} failed. Please try again."
							: installItem.StatusMessage;
						IsInstallErrorVisible = true;
						ApplyGameStatus();
						break;

					case ActionStatus.Cancelled:
						IsProgressRingIndeterminate = false;
						IsProgressRingVisible = false;
						ProgressValue = 0;
						IsPrimaryActionEnabled = true;
						ClearInstallError();
						ApplyGameStatus();
						break;
				}
			});
		}
		catch (Exception ex)
		{
			_log.LogError(ex, "HandleInstallationStatusChanged failed");
		}
	}

	private void CheckGameStatus(Game updatedGame)
	{
		if (updatedGame == null || updatedGame.AppName != Game?.AppName) return;
		_log.LogInformation("GameInfoPage: Game Status Changed for {Game}", updatedGame.AppTitle);
		Game = updatedGame;

		_uiDispatcher.TryEnqueue(ApplyGameStatus);
	}

	private void ApplyGameStatus()
	{
		if (Game is null)
			return;

		IsProgressRingVisible = false;
		IsPrimaryActionEnabled = true;
		PrimaryAction = GetPrimaryAction(Game.LocalAppState?.InstallStatus);
		if (PrimaryAction is GamePrimaryAction.Install)
		{
			PrimaryActionButtonText = "Install";
			IsInstalled = false;
			IsImportVisible = true;
			return;
		}

		IsImportVisible = false;
		switch (PrimaryAction)
		{
			case GamePrimaryAction.Play:
				PrimaryActionButtonText = "Play";
				IsInstalled = true;
				break;
			case GamePrimaryAction.Update:
				PrimaryActionButtonText = "Update";
				IsInstalled = true;
				break;
			case GamePrimaryAction.Repair:
				PrimaryActionButtonText = "Repair";
				IsInstalled = true;
				break;
		}
	}

	private void ClearInstallError()
	{
		IsInstallErrorVisible = false;
		InstallErrorMessage = string.Empty;
	}

	private static string GetActionLabel(ActionType action) => action switch
	{
		ActionType.Install => "Installation",
		ActionType.Update => "Update",
		ActionType.Repair or ActionType.Verify => "Repair",
		ActionType.Uninstall => "Uninstall",
		ActionType.Move => "Move",
		ActionType.Import => "Import",
		_ => "Operation"
	};

	internal static GamePrimaryAction GetPrimaryAction(InstallState? installState) => installState switch
	{
		null or InstallState.NotInstalled => GamePrimaryAction.Install,
		InstallState.Installed => GamePrimaryAction.Play,
		InstallState.NeedUpdate => GamePrimaryAction.Update,
		InstallState.Broken => GamePrimaryAction.Repair,
		_ => GamePrimaryAction.None
	};

	public void OnNavigatedFrom()
	{
		UnregisterEventHandlers();

	}

	[RelayCommand]
	private void Uninstall()
	{
		if (Game?.LocalAppState == null || Game.LocalAppState.InstallStatus == InstallState.NotInstalled) return;

		_storage.LocalAppStateDictionary.TryGetValue(Game.AppName, out var installedGame);

		if (installedGame == null)
		{
			_log.LogInformation("ProcessNext: Attempting to uninstall not installed game");
			return;
		}

		_installer.AddToQueue(new InstallItem(Game.AppName, ActionType.Uninstall, installedGame.InstallPath));
		_log.LogInformation("GameInfoPage: Added {Game} to Installation Queue", Game.AppTitle);
	}

	[RelayCommand]
	private void VerifyRepair()
	{
		if (Game?.LocalAppState == null || Game.LocalAppState.InstallStatus == InstallState.NotInstalled) return;

		_storage.LocalAppStateDictionary.TryGetValue(Game.AppName, out var installedGame);

		if (installedGame == null)
		{
			_log.LogInformation("VerifyRepair: Game not installed");
			return;
		}

		_log.LogInformation("GameInfoPage: Queueing verify/repair for {Game}", Game.AppTitle);
		_installer.AddToQueue(new InstallItem(Game.AppName, ActionType.Repair, installedGame.InstallPath));
	}

	[RelayCommand]
	private async Task ImportGameAsync()
	{
		if (Game == null) return;

		if (FolderPickerRequested == null)
		{
			_log.LogWarning("ImportGame: No folder picker handler registered");
			return;
		}

		var folderPath = await FolderPickerRequested.Invoke();
		if (string.IsNullOrEmpty(folderPath)) return;

		_log.LogInformation("GameInfoPage: Importing {Game} from {Path}", Game.AppTitle, folderPath);
		_installer.AddToQueue(new InstallItem(Game.AppName, ActionType.Import, folderPath));
	}

	[RelayCommand]
	private async Task MoveGameAsync()
	{
		if (Game?.LocalAppState == null || Game.LocalAppState.InstallStatus == InstallState.NotInstalled) return;

		if (FolderPickerRequested == null)
		{
			_log.LogWarning("MoveGame: No folder picker handler registered");
			return;
		}

		var folderPath = await FolderPickerRequested.Invoke();
		if (string.IsNullOrEmpty(folderPath)) return;

		var currentPath = Game.LocalAppState.InstallPath;
		var destPath = Path.Combine(folderPath, Game.AppName);

		_log.LogInformation("GameInfoPage: Moving {Game} from {Src} to {Dest}", Game.AppTitle, currentPath, destPath);
		var item = new InstallItem(Game.AppName, ActionType.Move, currentPath)
		{
			MoveLocation = destPath
		};
		_installer.AddToQueue(item);
	}

	public void Cleanup()
	{
		_libraryManager.GameStatusUpdated -= CheckGameStatus;
		_installer.InstallationStatusChanged -= HandleInstallationStatusChanged;
		_installer.InstallProgressUpdate -= HandleInstallationStatusChanged;
	}
}

