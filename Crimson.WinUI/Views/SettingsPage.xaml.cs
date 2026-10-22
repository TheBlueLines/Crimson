using Crimson.ViewModels;
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
	}
}
