using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crimson.Core;
using Crimson.Models;
using Crimson.Utils;
using Serilog;

namespace Crimson.ViewModels;

public partial class AppInstallDialogViewModel : ObservableObject
{
	private readonly InstallManager _installManager;
	private readonly LibraryManager _libraryManager;
	private readonly Storage _storageService;
	private readonly ILogger _logger;
	private readonly IUiDispatcher _uiDispatcher;
	private readonly SettingsManager? _settingsManager;

	private string _gameAppName;
	private InitializationState _initialization = new();

	private sealed class InitializationState(CancellationToken cancellationToken = default)
	{
		public CancellationToken CancellationToken { get; } = cancellationToken;
		public int DriveRequest;
		public double InstallSize;
	}

	[ObservableProperty]
	private string _gameTitle;

	[ObservableProperty]
	private string? _gameImageUrl;

	[ObservableProperty]
	private string _installLocation;

	[ObservableProperty]
	private bool _isLoadingContent;

	[ObservableProperty]
	private string _baseGameSize;

	[ObservableProperty]
	private string _totalDownloadSize;

	[ObservableProperty]
	private string _totalInstallSize;

	[ObservableProperty]
	private bool _isDriveSpaceVisible;

	[ObservableProperty]
	private double _driveSpaceUsagePercent;

	[ObservableProperty]
	private string _driveSpaceAvailable;

	[ObservableProperty]
	private string _driveTotalSpace;

	[ObservableProperty]
	private bool _canInstall;

	[ObservableProperty]
	private bool _hasDlcs;

	private bool _createDesktopShortcut;
	public bool CreateDesktopShortcut
	{
		get => _createDesktopShortcut;
		set => SetProperty(ref _createDesktopShortcut, value);
	}

	private bool _createStartMenuShortcut;
	public bool CreateStartMenuShortcut
	{
		get => _createStartMenuShortcut;
		set => SetProperty(ref _createStartMenuShortcut, value);
	}

	public ObservableCollection<DlcOption> AvailableDlcs { get; } = new();

	public event Action RequestClose;
	public event Func<Task<string>> FolderPickerRequested;

	public AppInstallDialogViewModel(
		ILogger logger,
		InstallManager installManager,
		LibraryManager libraryManager,
		Storage storage,
		IUiDispatcher uiDispatcher,
		SettingsManager? settingsManager = null)
	{
		_logger = logger;
		_installManager = installManager;
		_libraryManager = libraryManager;
		_storageService = storage;
		_uiDispatcher = uiDispatcher;
		_settingsManager = settingsManager;
	}

	public async Task InitializeAsync(Game gameInfo, CancellationToken cancellationToken = default)
	{
		var generation = new InitializationState(cancellationToken);
		Interlocked.Exchange(ref _initialization, generation);
		try
		{
			Activate();
			_gameAppName = gameInfo.AppName;
			GameTitle = gameInfo.AppTitle;
			GameImageUrl = SelectImageUrl(gameInfo);
			var defaultInstallLocation = _settingsManager?.DefaultInstallLocation
				?? _storageService.DefaultInstallPath;
			InstallLocation = Path.Combine(defaultInstallLocation, gameInfo.AppTitle);

			AvailableDlcs.Clear();
			var dlcs = _libraryManager.GetDlcsForGame(gameInfo.AppName);
			HasDlcs = dlcs.Count > 0;
			foreach (var dlc in dlcs)
			{
				AvailableDlcs.Add(new DlcOption
				{
					AppName = dlc.AppName,
					Title = dlc.AppTitle,
					IsSelected = true
				});
			}

			var sizes = await _installManager
				.GetGameDownloadInstallSizes(gameInfo.AppName, cancellationToken)
				.ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			if (!IsCurrentInitialization(generation))
				return;

			generation.InstallSize = sizes.totalWriteSizeMb;
			_uiDispatcher.TryEnqueue(() =>
			{
				if (!IsCurrentInitialization(generation))
					return;

				TotalDownloadSize = FormatSize(sizes.totalDownloadSizeMb);
				TotalInstallSize = FormatSize(sizes.totalWriteSizeMb);
			});

			await UpdateDriveSpace(InstallLocation, sizes.totalWriteSizeMb, generation).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			if (!IsCurrentInitialization(generation))
				return;

			_logger.Error(ex, "Failed to initialize install dialog");
			_uiDispatcher.TryEnqueue(() =>
			{
				if (!IsCurrentInitialization(generation))
					return;

				IsLoadingContent = false;
				RequestClose?.Invoke();
			});
		}
	}

	internal static string? SelectImageUrl(Game gameInfo)
	{
		var images = gameInfo.Metadata.KeyImages;
		return images.FirstOrDefault(image => image.Type == "DieselGameBoxTall")?.Url
			?? images.FirstOrDefault(image => image.Type == "DieselGameBox")?.Url;
	}

	public void Activate()
	{
		IsLoadingContent = true;
		CanInstall = false;
		IsDriveSpaceVisible = false;
		HasDlcs = false;
		CreateDesktopShortcut = false;
		CreateStartMenuShortcut = false;
		AvailableDlcs.Clear();
		TotalDownloadSize = "0 B";
		TotalInstallSize = "0 B";
		DriveSpaceAvailable = "0 B";
		DriveTotalSpace = "0 B";
		DriveSpaceUsagePercent = 0;
		Volatile.Read(ref _initialization).InstallSize = 0;
	}

	private async Task UpdateDriveSpace(string installLocation, double installSize, InitializationState generation)
	{
		var driveRequest = Interlocked.Increment(ref generation.DriveRequest);
		try
		{
			generation.CancellationToken.ThrowIfCancellationRequested();
			var driveInfo = await _storageService.GetDriveInfo(installLocation).ConfigureAwait(false);
			generation.CancellationToken.ThrowIfCancellationRequested();
			if (!IsCurrentRequest(generation, driveRequest))
				return;

			_uiDispatcher.TryEnqueue(() =>
			{
				if (IsCurrentRequest(generation, driveRequest))
					ApplyDriveSpace(driveInfo, installSize);
			});
		}
		catch (OperationCanceledException) when (generation.CancellationToken.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			if (!IsCurrentRequest(generation, driveRequest))
				return;

			_logger.Warning(ex, "Failed to get drive space info");
			_uiDispatcher.TryEnqueue(() =>
			{
				if (!IsCurrentRequest(generation, driveRequest))
					return;

				IsDriveSpaceVisible = false;
				CanInstall = false;
				IsLoadingContent = false;
			});
		}
	}

	private void ApplyDriveSpace(DriveInfo driveInfo, double installSize)
	{
		IsDriveSpaceVisible = true;
		var usedSpace = driveInfo.TotalSize - driveInfo.AvailableFreeSpace;
		DriveSpaceUsagePercent = ((double)usedSpace / driveInfo.TotalSize) * 100;
		DriveSpaceAvailable = FormatSize(driveInfo.AvailableFreeSpace);
		DriveTotalSpace = FormatSize(driveInfo.TotalSize);
		CanInstall = driveInfo.AvailableFreeSpace > installSize;
		IsLoadingContent = false;
	}

	private bool IsCurrentInitialization(InitializationState generation) =>
		ReferenceEquals(generation, Volatile.Read(ref _initialization));

	private bool IsCurrentRequest(InitializationState generation, int driveRequest) =>
		IsCurrentInitialization(generation) &&
		driveRequest == Volatile.Read(ref generation.DriveRequest);

	public void InvalidateInitialization()
	{
		Interlocked.Exchange(ref _initialization, new InitializationState());
	}

	[RelayCommand]
	private async Task SelectLocation()
	{
		var generation = Volatile.Read(ref _initialization);
		if (FolderPickerRequested != null)
		{
			var newPath = await FolderPickerRequested.Invoke();
			if (!string.IsNullOrEmpty(newPath) && IsCurrentInitialization(generation))
			{
				InstallLocation = Path.Combine(newPath, GameTitle);
				await UpdateDriveSpace(InstallLocation, generation.InstallSize, generation);
				await PersistDefaultInstallLocation(newPath);
			}
		}
	}

	private async Task PersistDefaultInstallLocation(string path)
	{
		if (_settingsManager is null)
			return;

		try
		{
			_settingsManager.DefaultInstallLocation = path;
			await _settingsManager.SaveSettings();
		}
		catch (Exception ex)
		{
			_logger.Warning(ex, "Failed to persist the default install location");
		}
	}

	[RelayCommand]
	private void CloseDialog()
	{
		InvalidateInitialization();
		RequestClose?.Invoke();
	}

	[RelayCommand]
	private void ConfirmInstall()
	{
		InvalidateInitialization();
		RequestClose?.Invoke();

		// Queue base game install
		_installManager.AddToQueue(new InstallItem(_gameAppName, ActionType.Install, InstallLocation)
		{
			CreateDesktopShortcut = CreateDesktopShortcut,
			CreateStartMenuShortcut = CreateStartMenuShortcut
		});
		_logger.Information("GameInfoViewModel: Added {Game} to Installation Queue", GameTitle);

		// Queue selected DLCs (install to same base path)
		foreach (var dlc in AvailableDlcs.Where(d => d.IsSelected))
		{
			_installManager.AddToQueue(new InstallItem(dlc.AppName, ActionType.Install, InstallLocation));
			_logger.Information("GameInfoViewModel: Added DLC {Dlc} to Installation Queue", dlc.Title);
		}
	}

	private string FormatSize(double bytes)
	{
		string[] sizes = ["B", "KB", "MB", "GB", "TB"];
		var order = 0;
		var size = bytes;

		while (size >= 1024 && order < sizes.Length - 1)
		{
			order++;
			size /= 1024;
		}

		return $"{size:0.##} {sizes[order]}";
	}
}

