using System;
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
			IFolderLauncher folderLauncher)
		{
			_settingsManager = settingsManager;
			_authManager = authManager;
			_folderLauncher = folderLauncher;
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
