using System;
using System.Threading.Tasks;
using Crimson.Models;
using Crimson.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace Crimson.Views;

/// <summary>
/// Native product page for a store offer.
/// </summary>
public sealed partial class StoreProductPage : Page
{
	public StoreProductViewModel ViewModel { get; }

	public StoreProductPage()
	{
		InitializeComponent();
		ViewModel = App.GetService<StoreProductViewModel>();
		DataContext = ViewModel;

		ViewModel.OpenWebStoreRequested += OpenWebStore;
		ViewModel.InstallDialogRequested += ShowInstallDialog;
		ViewModel.OpenInLibraryRequested += OpenInLibrary;
		ViewModel.OpenProductRequested += OpenProduct;
		ViewModel.OpenExternalLinkRequested += OpenExternalLink;
	}

	private void AddOn_Click(object sender, RoutedEventArgs e) =>
		ViewModel.OpenAddOnCommand.Execute((sender as FrameworkElement)?.DataContext as StoreItem);

	private void SocialLink_Click(object sender, RoutedEventArgs e) =>
		ViewModel.OpenSocialLinkCommand.Execute((sender as FrameworkElement)?.DataContext as StoreSocialLink);

	private void OpenProduct(StoreProductRequest request) =>
		Frame.Navigate(typeof(StoreProductPage), request, new DrillInNavigationTransitionInfo());

	private static async void OpenExternalLink(Uri uri) => await Windows.System.Launcher.LaunchUriAsync(uri);

	protected override async void OnNavigatedTo(NavigationEventArgs e)
	{
		base.OnNavigatedTo(e);
		await ViewModel.OnNavigatedTo(e.Parameter);
	}

	protected override void OnNavigatedFrom(NavigationEventArgs e)
	{
		ViewModel.OnNavigatedFrom();
		base.OnNavigatedFrom(e);
	}

	private void OpenWebStore(Uri uri) => Frame.Navigate(typeof(StoreWebPage), uri);

	private void OpenInLibrary(string appName) => Frame.Navigate(typeof(GameInfoPage), appName);

	private async Task ShowInstallDialog(Game game)
	{
		InstallDialog.XamlRoot = XamlRoot;
		await InstallDialog.ShowAsync(game);
	}
}
