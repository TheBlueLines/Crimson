using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Core;
using Crimson.Repository;
using Crimson.Utils;
using Crimson.ViewModels;
using Crimson.Views;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Polly;
using Serilog;
using Windows.ApplicationModel.Activation;

namespace Crimson
{
	/// <summary>
	/// Provides application-specific behavior to supplement the default Application class.
	/// </summary>
	public partial class App : Application
	{
		// The .NET Generic Host provides dependency injection, configuration, logging, and other services.
		// https://docs.microsoft.com/dotnet/core/extensions/generic-host
		// https://docs.microsoft.com/dotnet/core/extensions/dependency-injection
		// https://docs.microsoft.com/dotnet/core/extensions/configuration
		// https://docs.microsoft.com/dotnet/core/extensions/logging
		public IHost Host
		{
			get;
		}
		public static bool HandleClosedEvents { get; set; } = true;
		internal static AppActivationArguments? InitialActivationArguments { get; set; }

		private static readonly object ActivationLock = new();
		private static readonly Queue<AppActivationArguments> EarlyActivations = new();
		private static App? _currentInstance;
		private readonly Queue<AppActivationArguments> _pendingActivations = new();
		private readonly DispatcherQueue _dispatcherQueue;
		private readonly SemaphoreSlim _authenticationGate = new(1, 1);

		/// <summary>
		/// Initializes the singleton application object.  This is the first line of authored code
		/// executed, and as such is the logical equivalent of main() or WinMain().
		/// </summary>
		public App()
		{
			InitializeComponent();
			_dispatcherQueue = DispatcherQueue.GetForCurrentThread();
			lock (ActivationLock)
			{
				_currentInstance = this;
				while (EarlyActivations.TryDequeue(out var activation))
					_pendingActivations.Enqueue(activation);
			}
			this.UnhandledException += App_UnhandledException;

			Host = Microsoft.Extensions.Hosting.Host.
			CreateDefaultBuilder().
			UseContentRoot(AppContext.BaseDirectory).
			ConfigureServices((context, services) =>
			{
				services.AddSingleton<IUiDispatcher>(new WindowsUiDispatcher(_dispatcherQueue));
				services.AddSingleton<IFolderLauncher, WindowsFolderLauncher>();
				services.AddSingleton<ILogger>(provider =>
				{
					var logDirectory = Path.Combine(GetAppDataPath(), "logs");
					_ = Directory.CreateDirectory(logDirectory);
					var logFilePath = Path.Combine(logDirectory, $"{DateTime.Now:yyyy-MM-dd}.txt");

					var logger = new LoggerConfiguration()
						.MinimumLevel.Information()
						.WriteTo.File(
							logFilePath,
							rollingInterval: RollingInterval.Month,
							rollOnFileSizeLimit: true,
							retainedFileCountLimit: 30,
							outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
						)
						.CreateLogger();

					Log.Logger = logger;
					return logger;
				});
				services.AddHttpClient("EpicOAuth", ConfigureEpicClient)
					.ConfigurePrimaryHttpMessageHandler(CreateSecureHttpHandler);
				services.AddHttpClient("EpicApi", ConfigureEpicClient)
					.ConfigurePrimaryHttpMessageHandler(CreateSecureHttpHandler)
					.AddResilienceHandler(
						"CustomPipeline",
						static builder =>
						{
							// See: https://www.pollydocs.org/strategies/retry.html
							builder.AddRetry(new HttpRetryStrategyOptions
							{
								BackoffType = DelayBackoffType.Exponential,
								MaxRetryAttempts = 5,
								UseJitter = true
							});

							// See: https://www.pollydocs.org/strategies/timeout.html
							builder.AddTimeout(TimeSpan.FromSeconds(5));
						});
				services.AddHttpClient("EpicContent", ConfigureEpicClient)
					.ConfigurePrimaryHttpMessageHandler(CreateSecureHttpHandler);
				services.AddHttpClient("EpicStore", ConfigureEpicStoreClient)
					.ConfigurePrimaryHttpMessageHandler(CreateSecureHttpHandler);

				services.AddSingleton<Storage>(provider => new Storage(
					provider.GetRequiredService<ILogger>(),
					GetAppDataPath(),
					Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
				services.AddSingleton<SettingsManager>(provider => new SettingsManager(
					provider.GetRequiredService<Storage>(),
					provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SettingsManager>>(),
					@"C:\Games\",
					Path.Combine(GetAppDataPath(), "logs")));
				services.AddSingleton<ICredentialProtector, WindowsCredentialProtector>();
				services.AddSingleton<AuthManager>(provider => new AuthManager(
					provider.GetRequiredService<ILogger>(),
					provider.GetRequiredService<Storage>(),
					provider.GetRequiredService<ICredentialProtector>(),
					provider.GetRequiredService<IHttpClientFactory>().CreateClient("EpicOAuth")));
				services.AddSingleton<IStoreRepository>(provider => new EpicGamesRepository(
					provider.GetRequiredService<AuthManager>(),
					provider.GetRequiredService<ILogger>(),
					provider.GetRequiredService<IHttpClientFactory>().CreateClient("EpicApi"),
					provider.GetRequiredService<IHttpClientFactory>().CreateClient("EpicContent"),
					provider.GetRequiredService<IHttpClientFactory>().CreateClient("EpicStore")));
				services.AddSingleton<LibraryManager>();
				services.AddSingleton<IGameShortcutManager>(provider => new GameShortcutManager(
					provider.GetRequiredService<IHttpClientFactory>().CreateClient("EpicContent"),
					provider.GetRequiredService<ILogger>()));
				services.AddSingleton<IInstallPermissionChecker, FileSystemInstallPermissionChecker>();
				services.AddSingleton<InstallManager>();
				services.AddSingleton<DownloadManager>(provider => new DownloadManager(
					provider.GetRequiredService<ILogger>(),
					provider.GetRequiredService<IHttpClientFactory>().CreateClient("EpicContent")));

				services.AddTransient<SettingsViewModel>();
				services.AddTransient<DownloadsViewModel>();
				services.AddTransient<LibraryViewModel>();
				services.AddTransient<GameInfoViewModel>();
				services.AddTransient<AppInstallDialogViewModel>();
			}).
			Build();
		}

		private static string GetAppDataPath() => Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"Crimson");

		private static void ConfigureEpicClient(HttpClient client)
		{
			client.DefaultRequestHeaders.UserAgent.ParseAdd(
				"UELauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit");
			client.Timeout = TimeSpan.FromSeconds(100);
		}

		private static void ConfigureEpicStoreClient(HttpClient client)
		{
			client.DefaultRequestHeaders.UserAgent.ParseAdd(
				"EpicGamesLauncher/14.0.8-22004686+++Portal+Release-Live");
			client.Timeout = TimeSpan.FromSeconds(10);
		}

		private static HttpMessageHandler CreateSecureHttpHandler() => new HttpClientHandler
		{
			AllowAutoRedirect = true,
			UseCookies = false,
			MaxConnectionsPerServer = 16
		};

		/// <summary>
		/// Invoked when the application is launched normally by the end user.  Other entry points
		/// will be used such as when the application is launched to open a specific file.
		/// </summary>
		/// <param name="args">Details about the launch request and process.</param>
		protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
		{
			m_window = new MainWindow();
			m_window.Closed += OnExit;

			var activationArguments = InitialActivationArguments;
			InitialActivationArguments = null;
			if (activationArguments is not null && TryGetLaunchAppName(activationArguments, out var appName))
				await LaunchFromShortcutAsync(appName);
			else
			{
				ShowMainWindow();
				await EnsureAuthenticationAsync();
			}

			while (_pendingActivations.TryDequeue(out var pending))
				await ProcessActivationAsync(pending);
		}

		internal static void RouteActivation(AppActivationArguments arguments)
		{
			lock (ActivationLock)
			{
				if (_currentInstance is null)
				{
					EarlyActivations.Enqueue(arguments);
					return;
				}

				_currentInstance.HandleActivation(arguments);
			}
		}

		private void HandleActivation(AppActivationArguments arguments)
		{
			_dispatcherQueue.TryEnqueue(async () =>
			{
				if (m_window is null)
				{
					_pendingActivations.Enqueue(arguments);
					return;
				}

				await ProcessActivationAsync(arguments);
			});
		}

		private async Task ProcessActivationAsync(AppActivationArguments arguments)
		{
			if (TryGetLaunchAppName(arguments, out var appName))
				await LaunchFromShortcutAsync(appName);
			else
				ShowMainWindow();
		}

		private static bool TryGetLaunchAppName(AppActivationArguments arguments, out string appName)
		{
			if (arguments.Kind == ExtendedActivationKind.Protocol &&
				arguments.Data is IProtocolActivatedEventArgs protocolArguments)
				return GameLaunchRequest.TryParse(protocolArguments.Uri, out appName);

			if (arguments.Data is ILaunchActivatedEventArgs launchArguments)
				return GameLaunchRequest.TryParseCommandLine(launchArguments.Arguments, out appName);

			appName = string.Empty;
			return false;
		}

		private async Task LaunchFromShortcutAsync(string appName)
		{
			var authenticationStatus = await EnsureAuthenticationAsync();
			if (authenticationStatus == AuthenticationStatus.LoggedIn)
			{
				var libraryManager = GetService<LibraryManager>();
				var library = await libraryManager.GetLibraryData();
				if (library.Any(game => game.AppName == appName) &&
					await libraryManager.LaunchApp(appName))
					return;
			}

			ShowMainWindow();
		}

		private async Task<AuthenticationStatus> EnsureAuthenticationAsync()
		{
			await _authenticationGate.WaitAsync();
			try
			{
				var authManager = GetService<AuthManager>();
				return authManager.AuthenticationStatus == AuthenticationStatus.LoggedIn
					? AuthenticationStatus.LoggedIn
					: await authManager.CheckAuthStatus();
			}
			finally
			{
				_authenticationGate.Release();
			}
		}

		private void ShowMainWindow()
		{
			m_window?.Show();
			m_window?.Activate();
		}

		protected void OnExit(object sender, WindowEventArgs args)
		{
			if (HandleClosedEvents)
			{
				args.Handled = true;
				m_window.Hide();
			}
		}

		private Window? m_window;

		public Window GetWindow()
		{
			return m_window ?? throw new InvalidOperationException("The main window has not been created.");
		}
		public static T GetService<T>() where T : class
		{
			if ((App.Current as App)!.Host.Services.GetService(typeof(T)) is not T service)
			{
				throw new ArgumentException($"{typeof(T)} needs to be registered in ConfigureServices within App.xaml.cs.");
			}

			return service;
		}

		private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
		{
			var logger = Host?.Services?.GetService(typeof(ILogger)) as ILogger;
			logger?.Fatal(e.Exception, "Unhandled exception occurred");
		}
	}
}
