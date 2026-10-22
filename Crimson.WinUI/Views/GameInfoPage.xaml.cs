using System;
using System.Threading.Tasks;
using Crimson.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Crimson.Views;

public sealed partial class GameInfoPage : Page
{
	public GameInfoViewModel ViewModel { get; }

	public GameInfoPage()
	{
		InitializeComponent();
		ViewModel = App.GetService<GameInfoViewModel>();
		DataContext = ViewModel;

		// Subscribe to dialog and picker events
		ViewModel.ShowInstallDialogRequested += ShowInstallDialog;
		ViewModel.FolderPickerRequested += ShowFolderPicker;
	}

	protected override async void OnNavigatedTo(NavigationEventArgs e)
	{
		await ViewModel.OnNavigatedTo(e.Parameter);
	}

	protected override void OnNavigatedFrom(NavigationEventArgs e)
	{
		ViewModel.OnNavigatedFrom();
		base.OnNavigatedFrom(e);
	}

	private async Task ShowInstallDialog()
	{
		// Initialize the dialog
		InstallDialog.XamlRoot = this.XamlRoot;
		await InstallDialog.ShowAsync(ViewModel.Game);
	}

	private async Task<string> ShowFolderPicker()
	{
		var picker = new FolderPicker();
		picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
		picker.FileTypeFilter.Add("*");

		var window = ((App)Microsoft.UI.Xaml.Application.Current).GetWindow();
		var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
		WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

		var folder = await picker.PickSingleFolderAsync().AsTask();
		return folder?.Path;
	}
}
