using System;
using System.Text.Json;
using System.Threading.Tasks;
using Crimson.Core;
using Crimson.Utils;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Serilog;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Crimson.Views;

/// <summary>
/// An empty page that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class LoginPage : Page
{
	private readonly AuthManager _authManager = App.GetService<AuthManager>();
	private readonly ILogger _log;
	private readonly EpicLoginMessageGate _loginMessageGate = new();
	private const string EpicGamesLauncherVersion = "11.0.1-14907503+++Portal+Release-Live";
	private WebView2? _loginWebView;

	public LoginPage()
	{
		this.InitializeComponent();
		_log = App.GetService<ILogger>();
	}
	private async void WebView_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
	{
		const string jsCode = """
			window.ue = {
				signinprompt: {
					requestexchangecodesignin: function(exchangeCode) {
						const data = JSON.stringify({ type: 'set_exchange_code', code: exchangeCode });
						window.chrome.webview.postMessage(data);
					},
				},
				common: {
					launchexternalurl: function(url) {
						window.open(url, '_blank');
					}
				}
			};
			""";

		if (_loginWebView is null)
			return;

		await _loginWebView.ExecuteScriptAsync(jsCode);
	}
	private async void WebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
	{
		var message = e.TryGetWebMessageAsString();
		if (!_loginMessageGate.TryAccept(e.Source, message, out var exchangeCode))
		{
			_log.Warning("Login WebView rejected an invalid or replayed message");
			return;
		}

		await _authManager.DoExchangeLogin(exchangeCode);
		await ShareCookiesWithStoreAsync();
	}

	public async Task ShareCookiesWithStoreAsync()
	{
		var webView = _loginWebView;
		if (webView?.CoreWebView2 is null)
			return;

		try
		{
			var cookies = await webView.CoreWebView2.CookieManager
				.GetCookiesAsync("https://www.epicgames.com");
			foreach (var cookie in cookies)
			{
				if (!IsAuthenticationCookie(cookie.Name))
					continue;

				var shared = webView.CoreWebView2.CookieManager.CreateCookie(
					cookie.Name,
					cookie.Value,
					".epicgames.com",
					"/");
				shared.Expires = cookie.Expires;
				shared.IsHttpOnly = cookie.IsHttpOnly;
				shared.IsSecure = cookie.IsSecure;
				shared.SameSite = cookie.SameSite;
				webView.CoreWebView2.CookieManager.AddOrUpdateCookie(shared);
			}
		}
		catch (Exception ex)
		{
			_log.Warning(ex, "Failed to share the Epic web session with the store");
		}
	}

	private static bool IsAuthenticationCookie(string name) =>
		name is "EPIC_BEARER_TOKEN" or "EPIC_SESSION_AP" or "EPIC_SSO" or "EPIC_EG1";
	public async void InitWebView()
	{
		_log.Information("InitWebView: WebView Initializing");
		try
		{
			CloseWebView();

			var webView = new WebView2();
			WebViewHost.Children.Add(webView);
			_loginWebView = webView;

			await WebViewEnvironmentFactory.WaitForLoadedAsync(webView);

			var environment = await WebViewEnvironmentFactory.CreateAsync();
			await webView.EnsureCoreWebView2Async(environment);
			if (webView.CoreWebView2 is null)
			{
				_log.Error("InitWebView: CoreWebView2 was still null after initialisation");
				return;
			}

			webView.CoreWebView2.Settings.UserAgent = $"EpicGamesLauncher/{EpicGamesLauncherVersion}";

			webView.CoreWebView2.CookieManager.DeleteAllCookies();
			webView.NavigationStarting += WebView_NavigationStarting;
			webView.WebMessageReceived += WebView_WebMessageReceived;

			webView.Source = new Uri("https://www.epicgames.com/id/login");
		}
		catch (Exception ex)
		{
			_log.Error(ex, "InitWebView: failed to initialise the login WebView");
		}
	}
	public void CloseWebView()
	{
		if (_loginWebView is null)
			return;

		_loginWebView.NavigationStarting -= WebView_NavigationStarting;
		_loginWebView.WebMessageReceived -= WebView_WebMessageReceived;
		_loginWebView.Close();
		WebViewHost.Children.Remove(_loginWebView);
		_loginWebView = null;
	}
}
