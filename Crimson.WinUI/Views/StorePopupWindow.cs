using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Crimson.Utils;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Serilog;

namespace Crimson.Views;

internal sealed class StorePopupWindow : Window
{
	private static readonly HashSet<StorePopupWindow> OpenWindows = [];

	private readonly CoreWebView2Environment _environment;
	private readonly Func<string, Task<bool>> _handleLauncherRequest;
	private readonly ILogger _log;
	private readonly WebView2 _webView;

	private StorePopupWindow(
		CoreWebView2Environment environment,
		ILogger log,
		Func<string, Task<bool>> handleLauncherRequest)
	{
		_environment = environment;
		_log = log;
		_handleLauncherRequest = handleLauncherRequest;
		Title = "Epic Games Store";

		_webView = new WebView2();
		Content = _webView;
		Closed += OnClosed;
	}

	public CoreWebView2 CoreWebView2 => _webView.CoreWebView2 ??
		throw new InvalidOperationException("The popup WebView is not initialized");

	public static void CloseAll()
	{
		foreach (var window in new List<StorePopupWindow>(OpenWindows))
			window.Close();
	}

	public static async Task<StorePopupWindow> CreateAsync(
		CoreWebView2Environment environment,
		ILogger log,
		Func<string, Task<bool>> handleLauncherRequest)
	{
		var window = new StorePopupWindow(environment, log, handleLauncherRequest);
		OpenWindows.Add(window);
		window.Activate();

		try
		{
			await WebViewEnvironmentFactory.WaitForLoadedAsync(window._webView);
			await window._webView.EnsureCoreWebView2Async(environment);
			if (window._webView.CoreWebView2 is null)
				throw new InvalidOperationException("Popup CoreWebView2 was null after initialization");

			await StoreWebIntegration.ApplyAsync(window._webView.CoreWebView2);
			window._webView.CoreWebView2.DownloadStarting += window.DownloadStarting;
			window._webView.CoreWebView2.NewWindowRequested += window.NewWindowRequested;
			return window;
		}
		catch
		{
			window.Close();
			throw;
		}
	}

	private async void DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
	{
		var deferral = e.GetDeferral();
		try
		{
			if (await _handleLauncherRequest(e.DownloadOperation.Uri))
				e.Cancel = true;
		}
		catch (Exception ex)
		{
			_log.Error(ex, "StorePopupWindow: failed to handle a download request");
		}
		finally
		{
			deferral.Complete();
		}
	}

	private async void NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
	{
		var deferral = e.GetDeferral();
		e.Handled = true;

		try
		{
			if (await _handleLauncherRequest(e.Uri))
				return;

			var popup = await CreateAsync(_environment, _log, _handleLauncherRequest);
			e.NewWindow = popup.CoreWebView2;
		}
		catch (Exception ex)
		{
			_log.Error(ex, "StorePopupWindow: failed to open a nested popup");
		}
		finally
		{
			deferral.Complete();
		}
	}

	private void OnClosed(object sender, WindowEventArgs args)
	{
		Closed -= OnClosed;
		if (_webView.CoreWebView2 is not null)
		{
			_webView.CoreWebView2.DownloadStarting -= DownloadStarting;
			_webView.CoreWebView2.NewWindowRequested -= NewWindowRequested;
		}

		_webView.Close();
		OpenWindows.Remove(this);
	}
}
