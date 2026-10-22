using Crimson.Core;
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
