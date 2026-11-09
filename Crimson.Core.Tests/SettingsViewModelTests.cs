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
		var viewModel = new SettingsViewModel(settings, auth, folderLauncher);

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
		settings.CloseBehavior = CloseBehavior.RunInBackground;

		settings.CloseBehavior = (CloseBehavior)99;

		Assert.Equal(CloseBehavior.RunInBackground, settings.CloseBehavior);
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(3)]
	public void CloseBehaviorIndexIgnoresOutOfRangeSelections(int index)
	{
		var settings = CreateSettings();
		var viewModel = new SettingsViewModel(
			settings,
			new AuthManager(_logger, new Storage(_logger, _appDataPath, _appDataPath), new TestCredentialProtector(), new HttpClient()),
			new RecordingFolderLauncher());

		viewModel.CloseBehaviorIndex = index;

		Assert.Equal(CloseBehavior.Exit, settings.CloseBehavior);
	}

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

	private sealed class RecordingFolderLauncher : IFolderLauncher
	{
		public string? OpenedPath { get; private set; }

		public void Open(string path)
		{
			OpenedPath = path;
		}
	}
}
