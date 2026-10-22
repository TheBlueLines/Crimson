using System;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Models;
using Crimson.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace Crimson.Controls
{
	public sealed partial class AppInstallDialog : UserControl
	{
		private CancellationTokenSource? _initializationCancellation;

		public AppInstallDialogViewModel ViewModel { get; }
		public AppInstallDialog()
		{
			this.InitializeComponent();
			ViewModel = App.GetService<AppInstallDialogViewModel>();
			ViewModel.RequestClose += OnRequestClose;
			ViewModel.FolderPickerRequested += ShowFolderPicker;
		}

		public async Task ShowAsync(Game gameInfo)
		{
			var cancellation = new CancellationTokenSource();
			var previous = Interlocked.Exchange(ref _initializationCancellation, cancellation);
			previous?.Cancel();
			previous?.Dispose();
			try
			{
				_ = ObserveInitializationAsync(
					ViewModel.InitializeAsync(gameInfo, cancellation.Token),
					cancellation.Token);
				await InstallContentDialog.ShowAsync(ContentDialogPlacement.Popup);
			}
			catch (Exception ex)
			{
				App.GetService<ILogger>().Error(ex, "AppInstallDialog: Failed to show install dialog");
			}
			finally
			{
				if (ReferenceEquals(Interlocked.CompareExchange(
					ref _initializationCancellation, null, cancellation), cancellation))
				{
					cancellation.Cancel();
					cancellation.Dispose();
				}
				ViewModel.InvalidateInitialization();
			}
		}

		private static async Task ObserveInitializationAsync(
			Task initialization,
			CancellationToken cancellationToken)
		{
			try
			{
				await initialization;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
			}
			catch (Exception ex)
			{
				App.GetService<ILogger>().Error(ex, "AppInstallDialog: Initialization failed");
			}
		}

		private void OnRequestClose()
		{
			_initializationCancellation?.Cancel();
			InstallContentDialog?.Hide();
		}

		private async Task<string> ShowFolderPicker()
		{
			var folderPicker = new Windows.Storage.Pickers.FolderPicker();

			// Get the window handle for the current window
			var window = ((App)Application.Current).GetWindow();
			var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

			// Initialize the folder picker with the window handle
			WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);

			// Set folder picker options
			folderPicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop;
			folderPicker.FileTypeFilter.Add("*");

			// Show the picker and get the selected folder
			var folder = await folderPicker.PickSingleFolderAsync();

			return folder?.Path;
		}

		public void Cleanup()
		{
			var cancellation = Interlocked.Exchange(ref _initializationCancellation, null);
			cancellation?.Cancel();
			cancellation?.Dispose();
			ViewModel.InvalidateInitialization();
			ViewModel.RequestClose -= OnRequestClose;
			ViewModel.FolderPickerRequested -= ShowFolderPicker;
		}
	}
}
