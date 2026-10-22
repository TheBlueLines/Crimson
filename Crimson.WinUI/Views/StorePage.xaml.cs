using Crimson.Core;
using Crimson.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace Crimson.Views;

/// <summary>
/// Native Epic Games Store front and search results.
/// </summary>
public sealed partial class StorePage : Page
{
	private bool _hasLoaded;

	public StoreViewModel ViewModel { get; }

	public StorePage()
	{
		InitializeComponent();
		// Cached so coming back from a product keeps the results and scroll position.
		NavigationCacheMode = NavigationCacheMode.Enabled;
		ViewModel = App.GetService<StoreViewModel>();
		DataContext = ViewModel;
	}

	public void ShowSearch(string query) => _ = ViewModel.SearchAsync(query);

	protected override async void OnNavigatedTo(NavigationEventArgs e)
	{
		base.OnNavigatedTo(e);
		var resume = _hasLoaded && e.NavigationMode == NavigationMode.Back;
		_hasLoaded = true;
		if (resume)
			await ViewModel.ResumeAsync();
		else
			await ViewModel.OnNavigatedTo(e.Parameter);
	}

	protected override void OnNavigatedFrom(NavigationEventArgs e)
	{
		ViewModel.OnNavigatedFrom();
		base.OnNavigatedFrom(e);
	}

	private void StoreItem_Click(object sender, RoutedEventArgs e)
	{
		if (((FrameworkElement)sender).DataContext is not StoreItem item)
			return;

		Frame.Navigate(typeof(StoreProductPage), item.Offer.ToProductRequest(), new DrillInNavigationTransitionInfo());
	}

	private void StoreSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) =>
		ShowSearch(args.QueryText);

	private void RedeemButton_Click(object sender, RoutedEventArgs e) =>
		Frame.Navigate(typeof(StoreWebPage), StoreWebLinks.Redeem);
}
