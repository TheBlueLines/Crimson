using System;
using System.Text.Json;
using System.Threading.Tasks;
using Crimson.Models;
using Crimson.Utils;
using Microsoft.Extensions.Logging;

namespace Crimson.Core;

public class SettingsManager
{
	private readonly Storage _storage;
	private readonly ILogger<SettingsManager> _logger;
	private readonly string _defaultInstallLocation;
	private readonly string _logsDirectory;

	private Settings Settings { get; set; }

	public SettingsManager(
		Storage storage,
		ILogger<SettingsManager> logger,
		string defaultInstallLocation,
		string logsDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(defaultInstallLocation);
		ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);

		_storage = storage;
		_logger = logger;
		_defaultInstallLocation = defaultInstallLocation;
		_logsDirectory = logsDirectory;
		Settings = LoadSettings();
	}

	public bool MicEnabled { get => Settings.MicaEnabled; set { Settings.MicaEnabled = value; } }

	public string DefaultInstallLocation
	{
		get => Settings.DefaultInstallLocation ?? _defaultInstallLocation;
		set { Settings.DefaultInstallLocation = value; }
	}

	public string LogsDirectory => _logsDirectory;

	private Settings LoadSettings()
	{
		try
		{
			return JsonSerializer.Deserialize<Settings>(_storage.GetSettingsData()) ?? new Settings();

		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "LoadSettings failed");
			return new Settings();
		}
	}

	public async Task SaveSettings()
	{
		await _storage.SaveSettingsData(JsonSerializer.Serialize(Settings));

	}

}
