using Crimson.Core;
using Crimson.Models;
using Crimson.Utils;
using Crimson.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;

namespace Crimson.Tests;

public sealed class SettingsViewModelTests : IDisposable
{
	private readonly string _appDataPath = Path.Combine(
		Path.GetTempPath(),
		$"crimson-settings-{Guid.NewGuid():N}");
	private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

	[Fact]
	public void OpenLogsDirectoryUsesFolderLauncher()
	{
		var installPath = Path.Combine(_appDataPath, "games");
		var logsPath = Path.Combine(_appDataPath, "logs");
		var storage = new Storage(_logger, _appDataPath, installPath);
		var settings = new SettingsManager(
			storage,
			NullLogger<SettingsManager>.Instance,
			installPath,
			logsPath);
		var auth = new AuthManager(
			_logger,
			storage,
			new TestCredentialProtector(),
			new HttpClient());
		var folderLauncher = new RecordingFolderLauncher();
		var viewModel = new SettingsViewModel(settings, auth, folderLauncher, new FakeStartupRegistration());

		viewModel.OpenLogsDirectoryCommand.Execute(null);

		Assert.Equal(logsPath, folderLauncher.OpenedPath);
	}

	[Fact]
	public void CloseBehaviorDefaultsToExit()
	{
		Assert.Equal(CloseBehavior.Exit, CreateSettings().CloseBehavior);
	}

	[Fact]
	public async Task CloseBehaviorIsSavedAndReloaded()
	{
		var settings = CreateSettings();
		settings.CloseBehavior = CloseBehavior.MinimizeToTray;
		await settings.SaveSettings();

		Assert.Equal(CloseBehavior.MinimizeToTray, CreateSettings().CloseBehavior);
	}

	[Fact]
	public void CloseBehaviorIgnoresUnknownValues()
	{
		var settings = CreateSettings();
		settings.CloseBehavior = CloseBehavior.MinimizeToTaskbar;

		settings.CloseBehavior = (CloseBehavior)99;

		Assert.Equal(CloseBehavior.MinimizeToTaskbar, settings.CloseBehavior);
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(3)]
	public void CloseBehaviorIndexIgnoresOutOfRangeSelections(int index)
	{
		var settings = CreateSettings();
		var viewModel = CreateViewModel(settings, new FakeStartupRegistration());

		viewModel.CloseBehaviorIndex = index;

		Assert.Equal(CloseBehavior.Exit, settings.CloseBehavior);
	}

	[Fact]
	public void StartupBehaviorDefaultsToNone()
	{
		Assert.Equal(StartupBehavior.None, CreateSettings().StartupBehavior);
	}

	[Fact]
	public async Task StartupBehaviorIsSavedAndReloaded()
	{
		var settings = CreateSettings();
		settings.StartupBehavior = StartupBehavior.RunInBackground;
		await settings.SaveSettings();

		Assert.Equal(StartupBehavior.RunInBackground, CreateSettings().StartupBehavior);
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(3)]
	public void StartupBehaviorIndexIgnoresOutOfRangeSelections(int index)
	{
		var settings = CreateSettings();
		var registration = new FakeStartupRegistration();
		var viewModel = CreateViewModel(settings, registration);

		viewModel.StartupBehaviorIndex = index;

		Assert.Equal(StartupBehavior.None, settings.StartupBehavior);
		Assert.Empty(registration.Requests);
	}

	[Theory]
	[InlineData(StartupBehavior.RunInBackground)]
	[InlineData(StartupBehavior.OpenApp)]
	public void ChoosingAStartupOptionRegistersWithWindows(StartupBehavior behavior)
	{
		var registration = new FakeStartupRegistration();
		var viewModel = CreateViewModel(CreateSettings(), registration);

		viewModel.StartupBehaviorIndex = (int)behavior;

		Assert.Equal([true], registration.Requests);
	}

	[Fact]
	public void ChoosingNotToOpenRemovesTheRegistration()
	{
		var settings = CreateSettings();
		settings.StartupBehavior = StartupBehavior.OpenApp;
		var registration = new FakeStartupRegistration();
		var viewModel = CreateViewModel(settings, registration);

		viewModel.StartupBehaviorIndex = (int)StartupBehavior.None;

		Assert.Equal([false], registration.Requests);
	}

	[Fact]
	public void BlockedRegistrationFallsBackToNotOpeningAndExplainsWhy()
	{
		var settings = CreateSettings();
		var viewModel = CreateViewModel(settings, new FakeStartupRegistration { SetResult = false });
		var defaultDescription = viewModel.StartupBehaviorDescription;

		viewModel.StartupBehaviorIndex = (int)StartupBehavior.OpenApp;

		Assert.Equal(StartupBehavior.None, settings.StartupBehavior);
		Assert.Equal((int)StartupBehavior.None, viewModel.StartupBehaviorIndex);
		Assert.NotEqual(defaultDescription, viewModel.StartupBehaviorDescription);
	}

	[Fact]
	public async Task RefreshShowsNotOpeningAfterBeingTurnedOffInWindows()
	{
		var settings = CreateSettings();
		settings.StartupBehavior = StartupBehavior.OpenApp;
		var viewModel = CreateViewModel(settings, new FakeStartupRegistration { IsEnabled = false });

		await viewModel.RefreshStartupBehaviorAsync();

		Assert.Equal(StartupBehavior.None, settings.StartupBehavior);
	}

	[Fact]
	public async Task RefreshKeepsTheChoiceWhenWindowsCannotBeAsked()
	{
		var settings = CreateSettings();
		settings.StartupBehavior = StartupBehavior.OpenApp;
		var viewModel = CreateViewModel(settings, new FakeStartupRegistration { IsEnabled = null });

		await viewModel.RefreshStartupBehaviorAsync();

		Assert.Equal(StartupBehavior.OpenApp, settings.StartupBehavior);
	}

	[Theory]
	[InlineData("--startup", true)]
	[InlineData("\"C:\\Program Files\\Crimson\\Crimson.exe\" --startup", true)]
	[InlineData("", false)]
	[InlineData("--launch-game=Fortnite", false)]
	[InlineData("--startup-other", false)]
	public void StartupCommandLineIsRecognized(string arguments, bool expected)
	{
		Assert.Equal(expected, StartupLaunch.IsStartupCommandLine(arguments));
	}

	private SettingsViewModel CreateViewModel(SettingsManager settings, IStartupRegistration registration) => new(
		settings,
		new AuthManager(_logger, new Storage(_logger, _appDataPath, _appDataPath), new TestCredentialProtector(), new HttpClient()),
		new RecordingFolderLauncher(),
		registration);

	private SettingsManager CreateSettings() => new(
		new Storage(_logger, _appDataPath, Path.Combine(_appDataPath, "games")),
		NullLogger<SettingsManager>.Instance,
		Path.Combine(_appDataPath, "games"),
		Path.Combine(_appDataPath, "logs"));

	public void Dispose()
	{
		if (Directory.Exists(_appDataPath))
			Directory.Delete(_appDataPath, recursive: true);
	}

	private sealed class FakeStartupRegistration : IStartupRegistration
	{
		public List<bool> Requests { get; } = [];
		public bool SetResult { get; init; } = true;
		public bool? IsEnabled { get; init; } = true;

		public Task<bool> SetEnabledAsync(bool enabled)
		{
			Requests.Add(enabled);
			return Task.FromResult(SetResult);
		}

		public Task<bool?> IsEnabledAsync() => Task.FromResult(IsEnabled);
	}

	private sealed class RecordingFolderLauncher : IFolderLauncher
	{
		public string? OpenedPath { get; private set; }

		public void Open(string path)
		{
			OpenedPath = path;
		}
	}
}
