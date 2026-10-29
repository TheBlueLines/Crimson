using System;
using Crimson.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Crimson.Views
{
	/// <summary>
	/// Settings Page
	/// </summary>
	public sealed partial class SettingsPage : Page
	{
		public SettingsViewModel ViewModel => (SettingsViewModel)DataContext;

		public SettingsPage()
		{
			InitializeComponent();
			DataContext = App.GetService<SettingsViewModel>();
		}

		private static readonly Uri SourceCodeUri = new("https://github.com/TheBlueLines/Crimson");

		private async void SourceCodeCard_Click(object sender, RoutedEventArgs e) =>
			await Windows.System.Launcher.LaunchUriAsync(SourceCodeUri);
	}
}
