using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crimson.Core;
using Crimson.Models;

namespace Crimson.ViewModels
{
	public partial class SettingsViewModel : ObservableObject
	{
		private readonly SettingsManager _settingsManager;
		private readonly AuthManager _authManager;
		private readonly IFolderLauncher _folderLauncher;
		private readonly IStartupRegistration _startupRegistration;
		private readonly SemaphoreSlim _startupGate = new(1, 1);

		private const string StartupDescription = "Running in the background keeps Crimson in the system tray";
		private const string StartupBlockedDescription =
			"Windows is blocking Crimson from starting automatically. Allow it under Settings > Apps > Startup, then try again";

		public int Theme
		{
			get => _settingsManager.Theme;
			set
			{
				if (_settingsManager.Theme == value || value is < 0 or > 2) return;
				_settingsManager.Theme = value;
				OnPropertyChanged();
				_ = SaveSettingsAsync();
			}
		}

		public bool AcrylicEnabled
		{
			get => _settingsManager.AcrylicEnabled;
			set
			{
				_settingsManager.AcrylicEnabled = value;
				OnPropertyChanged();
				_ = SaveSettingsAsync();
			}
		}

		public bool CloudSaveAutoSync
		{
			get => _settingsManager.CloudSaveAutoSync;
			set
			{
				_settingsManager.CloudSaveAutoSync = value;
				OnPropertyChanged();
				_ = SaveSettingsAsync();
			}
		}

		public int CloseBehaviorIndex
		{
			get => (int)_settingsManager.CloseBehavior;
			set
			{
				var behavior = (CloseBehavior)value;
				if (_settingsManager.CloseBehavior == behavior || !Enum.IsDefined(behavior)) return;
				_settingsManager.CloseBehavior = behavior;
				OnPropertyChanged();
				_ = SaveSettingsAsync();
			}
		}

		public int StartupBehaviorIndex
		{
			get => (int)_settingsManager.StartupBehavior;
			set
			{
				var behavior = (StartupBehavior)value;
				if (_settingsManager.StartupBehavior == behavior || !Enum.IsDefined(behavior)) return;
				_settingsManager.StartupBehavior = behavior;
				OnPropertyChanged();
				_ = ApplyStartupBehaviorAsync();
			}
		}

		[ObservableProperty]
		private string _startupBehaviorDescription = StartupDescription;

		public string DefaultInstallLocation
		{
			get => _settingsManager.DefaultInstallLocation;
			set
			{
				_settingsManager.DefaultInstallLocation = value;
				OnPropertyChanged();
				_ = SaveSettingsAsync();
			}
		}

		public string LogsDirectory => _settingsManager.LogsDirectory;

		[ObservableProperty]
		private bool _advancedSettingsExpanded;

		public SettingsViewModel(
			SettingsManager settingsManager,
			AuthManager authManager,
			IFolderLauncher folderLauncher,
			IStartupRegistration startupRegistration)
		{
			_settingsManager = settingsManager;
			_authManager = authManager;
			_folderLauncher = folderLauncher;
			_startupRegistration = startupRegistration;
		}

		/// <summary>
		/// Shows "Don't open" if Crimson was turned off in Windows since the setting was chosen.
		/// </summary>
		public async Task RefreshStartupBehaviorAsync()
		{
			await _startupGate.WaitAsync();
			try
			{
				if (_settingsManager.StartupBehavior != StartupBehavior.None &&
					await _startupRegistration.IsEnabledAsync() == false)
					await ResetStartupBehaviorAsync();
			}
			finally
			{
				_startupGate.Release();
			}
		}

		private async Task ApplyStartupBehaviorAsync()
		{
			// One at a time, so quick changes cannot reach Windows out of order.
			await _startupGate.WaitAsync();
			try
			{
				// A failed disable needs no handling: a start with this setting off exits at once.
				var enabled = _settingsManager.StartupBehavior != StartupBehavior.None;
				var applied = await _startupRegistration.SetEnabledAsync(enabled);
				StartupBehaviorDescription = enabled && !applied ? StartupBlockedDescription : StartupDescription;
				if (enabled && !applied)
					await ResetStartupBehaviorAsync();
				else
					await SaveSettingsAsync();
			}
			finally
			{
				_startupGate.Release();
			}
		}

		private async Task ResetStartupBehaviorAsync()
		{
			_settingsManager.StartupBehavior = StartupBehavior.None;
			OnPropertyChanged(nameof(StartupBehaviorIndex));
			await SaveSettingsAsync();
		}

		[RelayCommand]
		private async Task Logout()
		{
			await _authManager.Logout();
		}

		private async Task SaveSettingsAsync()
		{
			await _settingsManager.SaveSettings();
		}

		[RelayCommand]
		private void OpenLogsDirectory()
		{
			_folderLauncher.Open(_settingsManager.LogsDirectory);
		}
	}
}
