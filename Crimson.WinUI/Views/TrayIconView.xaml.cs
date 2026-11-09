using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CommunityToolkit.Mvvm.Input;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Crimson.Views
{
	public sealed partial class TrayIconView : UserControl
	{
		public TrayIconView()
		{
			InitializeComponent();
		}

		public void SetIconVisible(bool visible)
		{
			// An icon created while marked hidden still appears, so it must exist before hiding it.
			// Creating also covers a window that never opens, where the icon's Loaded event never fires.
			TrayIcon.ForceCreate(enablesEfficiencyMode: false);
			TrayIcon.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
		}

		/// <summary>
		/// Removes the icon. Its context menu window would otherwise keep the process alive.
		/// </summary>
		public void RemoveIcon() => TrayIcon.Dispose();

		[RelayCommand]
		public void ShowHideWindow()
		{
			var app = (App)Application.Current;
			if (app.IsMainWindowShown)
			{
				app.HideMainWindow();
			}
			else
			{
				app.ShowMainWindow();
			}
		}

		[RelayCommand]
		public void ExitApplication()
		{
			((App)Application.Current).ExitApplication();
		}
	}
}
