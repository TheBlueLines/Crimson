using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crimson.Core;

namespace Crimson.ViewModels
{
	public partial class SettingsViewModel : ObservableObject
	{
		private readonly SettingsManager _settingsManager;
		private readonly AuthManager _authManager;
		private readonly IFolderLauncher _folderLauncher;

		public bool MicaEnabled
		{
			get => _settingsManager.MicEnabled;
			set
			{
				_settingsManager.MicEnabled = value;
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
