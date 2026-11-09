using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;

namespace Crimson.Utils;

public static class WebViewEnvironmentFactory
{
	private static readonly object EnvironmentLock = new();
	private static Task<CoreWebView2Environment>? _environmentTask;

	public static Task<CoreWebView2Environment> CreateAsync()
	{
		lock (EnvironmentLock)
		{
			if (_environmentTask is null || _environmentTask.IsFaulted || _environmentTask.IsCanceled)
				_environmentTask = CreateCoreAsync();

			return _environmentTask;
		}
	}

	private static async Task<CoreWebView2Environment> CreateCoreAsync()
	{
		var userDataFolder = Path.Combine(AppDataPaths.Root, "webview2");
		Directory.CreateDirectory(userDataFolder);

		return await CoreWebView2Environment.CreateWithOptionsAsync(
			string.Empty, userDataFolder, new CoreWebView2EnvironmentOptions());
	}

	/// <summary>
	/// Crimson has no use for a camera or microphone, so pages never get to prompt for them.
	/// </summary>
	public static void DenyMediaCapture(CoreWebView2 webView)
	{
		webView.PermissionRequested += (_, args) =>
		{
			if (args.PermissionKind is CoreWebView2PermissionKind.Camera or CoreWebView2PermissionKind.Microphone)
				args.State = CoreWebView2PermissionState.Deny;
		};
	}

	public static Task WaitForLoadedAsync(FrameworkElement element)
	{
		if (element.IsLoaded)
			return Task.CompletedTask;

		var loaded = new TaskCompletionSource();

		void OnLoaded(object sender, RoutedEventArgs args)
		{
			element.Loaded -= OnLoaded;
			loaded.TrySetResult();
		}

		element.Loaded += OnLoaded;
		return loaded.Task;
	}
}
