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
		public Dictionary<string, Manifest> Manifests { get; } = new();
		public List<string>? InstalledTags;
		public HashSet<string> InstalledDlcs { get; } = new();
	}

	private double _availableBytes;
	private bool _isManageContent;
	public bool IsManageContent
	{
		get => _isManageContent;
		private set
		{
			SetProperty(ref _isManageContent, value);
			OnPropertyChanged(nameof(IsNewInstallation));
			OnPropertyChanged(nameof(ConfirmButtonText));
			OnPropertyChanged(nameof(DiskSizeLabel));
		}
	}
	public bool IsNewInstallation => !IsManageContent;
	public string ConfirmButtonText => IsManageContent ? "Apply" : "Install";
	public string DiskSizeLabel => IsManageContent ? "disk space change" : "install size";
	public ObservableCollection<DlcOption> AvailableInstallTags { get; } = new();

	[ObservableProperty]
	private string _selectionError = string.Empty;

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
			IsManageContent = gameInfo.LocalAppState?.InstallStatus is not null and not InstallState.NotInstalled;
			generation.InstalledTags = gameInfo.LocalAppState?.SelectedInstallTags is { } tags ? [.. tags] : null;
			GameTitle = gameInfo.AppTitle;
			GameImageUrl = SelectImageUrl(gameInfo);
			var defaultInstallLocation = _settingsManager?.DefaultInstallLocation
				?? _storageService.DefaultInstallPath;
			InstallLocation = IsManageContent ? gameInfo.LocalAppState!.InstallPath : Path.Combine(defaultInstallLocation, gameInfo.AppTitle);

			AvailableDlcs.Clear();
			var dlcs = _libraryManager.GetDlcsForGame(gameInfo.AppName);
			foreach (var dlc in dlcs)
			{
				if (dlc.LocalAppState?.InstallStatus is InstallState.Installed or InstallState.NeedUpdate)
					generation.InstalledDlcs.Add(dlc.AppName);
			}

			var manifest = await _installManager.GetContentManifestAsync(gameInfo.AppName, IsManageContent, cancellationToken).ConfigureAwait(false);
			generation.Manifests[gameInfo.AppName] = manifest;
			var installableDlcs = new List<Game>();
			foreach (var dlc in dlcs)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!IsCurrentInitialization(generation))
					return;
				if (dlc.AssetInfos?.Windows is null)
					continue;
				try
				{
					var dlcManifest = await _installManager.GetContentManifestAsync(
						dlc.AppName, IsManageContent && generation.InstalledDlcs.Contains(dlc.AppName), cancellationToken).ConfigureAwait(false);
					// Entitlements such as cosmetic packs have nothing to install separately.
					if (dlcManifest.FileManifestList.Elements.Count == 0)
						continue;
					generation.Manifests[dlc.AppName] = dlcManifest;
					installableDlcs.Add(dlc);
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					throw;
				}
				catch (Exception ex)
				{
					_logger.Warning(ex, "Skipping DLC without an available content manifest: {AppName}", dlc.AppName);
				}
			}
			var sizes = InstallManager.CalculateManifestSizes(manifest);
			cancellationToken.ThrowIfCancellationRequested();
			if (!IsCurrentInitialization(generation))
				return;

			generation.InstallSize = sizes.WriteBytes;
			_uiDispatcher.TryEnqueue(() =>
			{
				if (!IsCurrentInitialization(generation))
					return;

				BaseGameSize = IsManageContent ? "Installed" : FormatSize(sizes.WriteBytes);
				AvailableDlcs.Clear();
				foreach (var dlc in installableDlcs)
				{
					AvailableDlcs.Add(new DlcOption
					{
						AppName = dlc.AppName,
						Title = dlc.AppTitle,
						IsSelected = !IsManageContent || generation.InstalledDlcs.Contains(dlc.AppName)
					});
				}
				HasDlcs = AvailableDlcs.Count > 0;
				AvailableInstallTags.Clear();
				foreach (var tag in manifest.FileManifestList.Elements.SelectMany(file => file.InstallTags)
					.Where(tag => !string.IsNullOrEmpty(tag)).Distinct(StringComparer.Ordinal).Order())
				{
					var option = new DlcOption
					{
						AppName = tag,
						Title = tag,
						IsSelected = generation.InstalledTags is null || generation.InstalledTags.Contains(tag)
					};
					option.PropertyChanged += (_, _) => UpdateSelectionSizes(generation);
					AvailableInstallTags.Add(option);
				}
				foreach (var option in AvailableDlcs)
					option.PropertyChanged += (_, _) => UpdateSelectionSizes(generation);
				UpdateSelectionSizes(generation);
			});

			await UpdateDriveSpace(InstallLocation, generation).ConfigureAwait(false);
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
		AvailableInstallTags.Clear();
		SelectionError = string.Empty;
		TotalDownloadSize = "0 B";
		TotalInstallSize = "0 B";
		DriveSpaceAvailable = "0 B";
		DriveTotalSpace = "0 B";
		DriveSpaceUsagePercent = 0;
		_availableBytes = 0;
		Volatile.Read(ref _initialization).InstallSize = 0;
	}

	private async Task UpdateDriveSpace(string installLocation, InitializationState generation)
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
					ApplyDriveSpace(driveInfo, generation.InstallSize);
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
		_availableBytes = driveInfo.AvailableFreeSpace;
		CanInstall = _availableBytes >= installSize;
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
		if (IsManageContent)
			return;
		var generation = Volatile.Read(ref _initialization);
		if (FolderPickerRequested != null)
		{
			var newPath = await FolderPickerRequested.Invoke();
			if (!string.IsNullOrEmpty(newPath) && IsCurrentInitialization(generation))
			{
				InstallLocation = Path.Combine(newPath, GameTitle);
				await UpdateDriveSpace(InstallLocation, generation);
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
		if (!CanInstall || IsLoadingContent)
			return;
		if (IsManageContent)
		{
			var generation = Volatile.Read(ref _initialization);
			var visibleDlcs = AvailableDlcs.Select(option => option.AppName).ToHashSet(StringComparer.Ordinal);
			// Hidden or unavailable installed DLC must remain untouched by this dialog.
			var selectedDlcs = AvailableDlcs.Where(option => option.IsSelected).Select(option => option.AppName)
				.Concat(generation.InstalledDlcs.Where(appName => !visibleDlcs.Contains(appName)));
			if (!_installManager.TryQueueContentChange(_gameAppName,
				AvailableInstallTags.Where(option => option.IsSelected).Select(option => option.AppName),
				selectedDlcs))
			{
				SelectionError = "The game must be installed and idle to change its content.";
				return;
			}
			InvalidateInitialization();
			RequestClose?.Invoke();
			return;
		}
		InvalidateInitialization();
		RequestClose?.Invoke();

		// Queue base game install
		_installManager.AddToQueue(new InstallItem(_gameAppName, ActionType.Install, InstallLocation)
		{
			SelectedInstallTags = AvailableInstallTags.Where(option => option.IsSelected).Select(option => option.AppName).ToList(),
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

	private void UpdateSelectionSizes(InitializationState generation)
	{
		if (!IsCurrentInitialization(generation) || !generation.Manifests.TryGetValue(_gameAppName, out var manifest))
			return;
		var selected = InstalledContentSelection.Filter(manifest.FileManifestList.Elements,
			AvailableInstallTags.Where(option => option.IsSelected).Select(option => option.AppName).ToList());
		var before = IsManageContent
			? InstalledContentSelection.Filter(manifest.FileManifestList.Elements, generation.InstalledTags)
			: new List<FileManifest>();
		var beforeNames = before.Select(file => file.Filename).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var added = selected.Where(file => !beforeNames.Contains(file.Filename));
		var sizes = InstallManager.CalculateManifestSizes(InstalledContentSelection.WithFiles(manifest, added));
		var download = sizes.DownloadBytes;
		var beforeFiles = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
		var afterFiles = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
		AddFiles(beforeFiles, InstallLocation, before);
		AddFiles(afterFiles, InstallLocation, selected);
		var requiredSpace = sizes.WriteBytes;
		foreach (var dlc in AvailableDlcs)
		{
			var wasInstalled = IsManageContent && generation.InstalledDlcs.Contains(dlc.AppName);
			if (!generation.Manifests.TryGetValue(dlc.AppName, out var dlcManifest))
				continue;
			var state = _libraryManager.GetGameInfo(dlc.AppName).LocalAppState;
			var files = InstalledContentSelection.Filter(dlcManifest.FileManifestList.Elements, state?.SelectedInstallTags);
			var location = wasInstalled && !string.IsNullOrWhiteSpace(state?.InstallPath) ? state.InstallPath : InstallLocation;
			if (wasInstalled)
				AddFiles(beforeFiles, location, files);
			if (dlc.IsSelected)
				AddFiles(afterFiles, location, files);
			if (dlc.IsSelected == wasInstalled)
				continue;
			var dlcSizes = InstallManager.CalculateManifestSizes(InstalledContentSelection.WithFiles(dlcManifest, files));
			if (dlc.IsSelected)
			{
				download += dlcSizes.DownloadBytes;
				requiredSpace += dlcSizes.WriteBytes;
			}
		}
		var diskChange = (double)afterFiles.Values.Sum() - beforeFiles.Values.Sum();
		generation.InstallSize = requiredSpace;
		TotalDownloadSize = FormatSize(download);
		TotalInstallSize = diskChange < 0 ? $"-{FormatSize(-diskChange)}" : FormatSize(diskChange);
		if (!IsLoadingContent)
			CanInstall = IsDriveSpaceVisible && _availableBytes >= requiredSpace;
	}

	private static void AddFiles(Dictionary<string, long> files, string location, IEnumerable<FileManifest> manifests)
	{
		foreach (var file in manifests)
		{
			var path = Path.Combine(location, file.Filename).Replace('\\', '/');
			files[path] = Math.Max(files.GetValueOrDefault(path), file.FileSize);
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

