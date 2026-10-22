using System;
using System.Linq;
using System.Threading.Tasks;
using Crimson.Core;
using Crimson.Utils;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using Serilog;

namespace Crimson.Views;

/// <summary>
/// The Epic web store, embedded with the login session. The native store only sends
/// people here for checkout, code redemption, and pages the catalog API cannot render.
/// </summary>
public sealed partial class StoreWebPage : Page
{
	private static readonly Uri StoreUri = StoreWebLinks.Home;

	private readonly ILogger _log = App.GetService<ILogger>();
	private readonly LibraryManager _libraryManager = App.GetService<LibraryManager>();
	private bool _cancelledLauncherNavigation;
	private Uri _targetUri = StoreUri;
	private CoreWebView2Environment? _environment;
	private WebView2? _storeWebView;
	private bool _ownershipRefreshed;

	public StoreWebPage()
	{
		InitializeComponent();
	}

	public bool TryGoBack()
	{
		if (_storeWebView?.CanGoBack != true)
			return false;

		_storeWebView.GoBack();
		return true;
	}

	protected override async void OnNavigatedTo(NavigationEventArgs e)
	{
		_targetUri = e.Parameter is Uri uri && EpicEndpointPolicy.IsAllowedStoreUri(uri)
			? uri
			: StoreUri;
		await InitializeWebViewAsync();
	}

	protected override void OnNavigatedFrom(NavigationEventArgs e)
	{
		if (!_ownershipRefreshed)
			_libraryManager.InvalidateCache();
		StorePopupWindow.CloseAll();
		CloseWebView();
		base.OnNavigatedFrom(e);
	}

	private async Task InitializeWebViewAsync()
	{
		if (_storeWebView is not null)
			return;

		ErrorPanel.Visibility = Visibility.Collapsed;
		LoadingRing.IsActive = true;

		var webView = new WebView2();
		WebViewHost.Children.Add(webView);
		_storeWebView = webView;

		try
		{
			await WebViewEnvironmentFactory.WaitForLoadedAsync(webView);
			if (!ReferenceEquals(_storeWebView, webView))
				return;

			var environment = await WebViewEnvironmentFactory.CreateAsync();
			if (!ReferenceEquals(_storeWebView, webView))
				return;

			_environment = environment;
			await webView.EnsureCoreWebView2Async(environment);
			if (!ReferenceEquals(_storeWebView, webView))
				return;

			if (webView.CoreWebView2 is null)
				throw new InvalidOperationException("CoreWebView2 was null after initialization");

			webView.CoreWebView2.Settings.UserAgent =
				"EpicGamesLauncher/11.0.1-14907503+++Portal+Release-Live";
			await StoreWebIntegration.ApplyAsync(webView.CoreWebView2);
			webView.NavigationStarting += StoreNavigationStarting;
			webView.NavigationCompleted += StoreNavigationCompleted;
			webView.CoreWebView2.DownloadStarting += StoreDownloadStarting;
			webView.CoreWebView2.NewWindowRequested += StoreNewWindowRequested;
			webView.Source = _targetUri;
		}
		catch (Exception ex)
		{
			if (!ReferenceEquals(_storeWebView, webView))
				return;

			_log.Error(ex, "StoreWebPage:failed to initialize the store WebView");
			ShowError("Check your connection and WebView2 Runtime, then try again.");
		}
	}

	private async void StoreNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
	{
		if (IsLauncherRequest(e.Uri))
		{
			_cancelledLauncherNavigation = true;
			e.Cancel = true;
			await HandleLauncherRequestAsync(e.Uri);
			return;
		}

		LoadingRing.IsActive = true;
		ErrorPanel.Visibility = Visibility.Collapsed;
	}

	private void StoreNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
	{
		if (_cancelledLauncherNavigation)
		{
			_cancelledLauncherNavigation = false;
			return;
		}

		LoadingRing.IsActive = false;

		if (!e.IsSuccess)
		{
			_log.Warning("StoreWebPage:navigation failed with {WebErrorStatus}", e.WebErrorStatus);
			ShowError("The Epic Games Store did not finish loading. Try again.");
		}
	}

	private async void StoreDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
	{
		if (!IsLauncherRequest(e.DownloadOperation.Uri))
			return;

		e.Cancel = true;
		await HandleLauncherRequestAsync(e.DownloadOperation.Uri);
	}

	private async void StoreNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
	{
		var deferral = e.GetDeferral();
		e.Handled = true;

		try
		{
			if (await HandleLauncherRequestAsync(e.Uri) || _environment is null)
				return;

			var popup = await StorePopupWindow.CreateAsync(
				_environment, _log, HandleLauncherRequestAsync);
			e.NewWindow = popup.CoreWebView2;
		}
		catch (Exception ex)
		{
			_log.Error(ex, "StoreWebPage:failed to open an embedded popup");
		}
		finally
		{
			deferral.Complete();
		}
	}

	private async Task<bool> HandleLauncherRequestAsync(string rawUri)
	{
		if (!Uri.TryCreate(rawUri, UriKind.Absolute, out var uri) || !IsLauncherRequest(uri))
			return false;

		var catalogItemId = uri.Scheme.Equals("com.epicgames.launcher", StringComparison.OrdinalIgnoreCase)
			? GetQueryParameter(uri, "catalogItemId")
			: null;
		var sandboxId = GetQueryParameter(uri, "sandboxId");
		var productSlug = GetProductSlug(_storeWebView?.Source);

		LoadingRing.IsActive = true;
		try
		{
			_libraryManager.InvalidateCache();
			var games = await _libraryManager.GetLibraryData();
			_ownershipRefreshed = true;
			var game = games.FirstOrDefault(candidate =>
				(catalogItemId is not null &&
				 string.Equals(candidate.AssetInfos?.Windows?.CatalogItemId, catalogItemId, StringComparison.OrdinalIgnoreCase) &&
				 (sandboxId is null || string.Equals(candidate.AssetInfos?.Windows?.Namespace, sandboxId, StringComparison.OrdinalIgnoreCase))) ||
				(productSlug is not null &&
				 string.Equals(candidate.Metadata?.CustomAttributes?.ComEpicgamesAppProductSlug?.Value, productSlug, StringComparison.OrdinalIgnoreCase)));

			LoadingRing.IsActive = false;
			if (game is not null)
				Frame.Navigate(typeof(GameInfoPage), game.AppName);
			else
				Frame.Navigate(typeof(LibraryPage));
		}
		catch (Exception ex)
		{
			LoadingRing.IsActive = false;
			_log.Error(ex, "StoreWebPage:failed to open an Epic launcher request");
			ShowError("Your library could not be refreshed. Try again.");
		}

		return true;
	}

	private static bool IsLauncherRequest(string rawUri)
	{
		return Uri.TryCreate(rawUri, UriKind.Absolute, out var uri) && IsLauncherRequest(uri);
	}

	private static bool IsLauncherRequest(Uri uri)
	{
		if (uri.Scheme.Equals("com.epicgames.launcher", StringComparison.OrdinalIgnoreCase))
			return true;

		return uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
			   uri.Host.EndsWith(".ol.epicgames.com", StringComparison.OrdinalIgnoreCase) &&
			   uri.AbsolutePath.Contains("/launcher/api/installer/download/", StringComparison.OrdinalIgnoreCase);
	}

	private static string? GetQueryParameter(Uri uri, string name)
	{
		foreach (var parameter in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
		{
			var parts = parameter.Split('=', 2);
			if (Uri.UnescapeDataString(parts[0]).Equals(name, StringComparison.OrdinalIgnoreCase))
				return parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
		}

		return null;
	}

	private static string? GetProductSlug(Uri? uri)
	{
		if (uri is null)
			return null;

		var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
		for (var i = 0; i < segments.Length - 1; i++)
		{
			if (segments[i].Equals("p", StringComparison.OrdinalIgnoreCase))
				return Uri.UnescapeDataString(segments[i + 1]);
		}

		return null;
	}

	private async void RetryButton_Click(object sender, RoutedEventArgs e)
	{
		CloseWebView();
		await InitializeWebViewAsync();
	}

	private void ShowError(string message)
	{
		LoadingRing.IsActive = false;
		ErrorMessage.Text = message;
		ErrorPanel.Visibility = Visibility.Visible;
	}

	private void CloseWebView()
	{
		var webView = _storeWebView;
		if (webView is null)
			return;

		_storeWebView = null;
		_environment = null;
		webView.NavigationStarting -= StoreNavigationStarting;
		webView.NavigationCompleted -= StoreNavigationCompleted;

		if (webView.CoreWebView2 is not null)
		{
			webView.CoreWebView2.DownloadStarting -= StoreDownloadStarting;
			webView.CoreWebView2.NewWindowRequested -= StoreNewWindowRequested;
		}

		webView.Close();
		WebViewHost.Children.Remove(webView);
	}
}
