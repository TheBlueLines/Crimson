using Crimson.Core;
using Crimson.Models;
using Crimson.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;

namespace Crimson.Tests;

public sealed class StorageTests : IDisposable
{
	private readonly string _appDataPath = Path.Combine(
		Path.GetTempPath(),
		$"crimson-storage-{Guid.NewGuid():N}");
	private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

	[Fact]
	public async Task UsesInjectedApplicationDataPath()
	{
		var storage = new Storage(_logger, _appDataPath, _appDataPath);

		await storage.SaveSettingsData("test settings");

		Assert.Equal("test settings", storage.GetSettingsData());
		Assert.True(File.Exists(Path.Combine(_appDataPath, "settings.json")));
		Assert.True(Directory.Exists(Path.Combine(_appDataPath, "metadata")));
		Assert.True(Directory.Exists(Path.Combine(_appDataPath, "manifests")));
		Assert.Equal(Path.GetFullPath(_appDataPath), storage.DefaultInstallPath);
	}

	[Fact]
	public void SettingsManagerUsesInjectedPlatformPaths()
	{
		var installPath = Path.Combine(_appDataPath, "games");
		var logsPath = Path.Combine(_appDataPath, "logs");
		var storage = new Storage(_logger, _appDataPath, installPath);
		var settings = new SettingsManager(
			storage,
			NullLogger<SettingsManager>.Instance,
			installPath,
			logsPath);

		Assert.Equal(installPath, settings.DefaultInstallLocation);
		Assert.Equal(logsPath, settings.LogsDirectory);
	}

	[Fact]
	public async Task ThemeDefaultsToSystemAndPersistsSelection()
	{
		var storage = new Storage(_logger, _appDataPath, _appDataPath);
		SettingsManager CreateSettings() => new(storage, NullLogger<SettingsManager>.Instance, _appDataPath, _appDataPath);
		var settings = CreateSettings();
		Assert.Equal(0, settings.Theme);
		var changes = new List<int>();
		settings.ThemeChanged += changes.Add;
		settings.Theme = 1;
		settings.Theme = 1;
		settings.Theme = 99;
		await settings.SaveSettings();
		Assert.Equal(new[] { 1 }, changes);
		Assert.Equal(1, CreateSettings().Theme);
	}

	[Fact]
	public async Task InvalidThemeCanBeResetToSystem()
	{
		var storage = new Storage(_logger, _appDataPath, _appDataPath);
		await storage.SaveSettingsData("{\"Theme\":99}");
		var settings = new SettingsManager(storage, NullLogger<SettingsManager>.Instance, _appDataPath, _appDataPath);
		Assert.Equal(0, settings.Theme);
		settings.Theme = 0;
		await settings.SaveSettings();
		Assert.Contains("\"Theme\":0", storage.GetSettingsData());
	}

	[Fact]
	public void RejectsMetadataPathOutsideInjectedRoot()
	{
		var storage = new Storage(_logger, _appDataPath, _appDataPath);
		var fileName = $"escape-{Guid.NewGuid():N}";
		var outsidePath = Path.Combine(Directory.GetParent(_appDataPath)!.FullName, $"{fileName}.json");
		var game = new Game
		{
			AppName = Path.Combine("..", "..", fileName),
			AppTitle = "Escape",
			AssetInfos = null!,
			Metadata = null!
		};

		Assert.Throws<InvalidOperationException>(() => storage.SaveMetaData(game));
		Assert.False(File.Exists(outsidePath));
	}

	public void Dispose()
	{
		if (Directory.Exists(_appDataPath))
			Directory.Delete(_appDataPath, recursive: true);
	}
}
