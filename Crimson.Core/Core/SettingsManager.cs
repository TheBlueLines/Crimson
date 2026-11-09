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

	public event Action<bool>? AcrylicEnabledChanged;
	public event Action<int>? ThemeChanged;
	public event Action<CloseBehavior>? CloseBehaviorChanged;

	// 0 follows Windows, 1 is light and 2 is dark. Unknown saved values fall back to System.
	public int Theme
	{
		get => Settings.Theme is >= 0 and <= 2 ? Settings.Theme : 0;
		set
		{
			if (value is < 0 or > 2 || Settings.Theme == value) return;
			Settings.Theme = value;
			ThemeChanged?.Invoke(value);
		}
	}

	public bool AcrylicEnabled
	{
		get => Settings.AcrylicEnabled;
		set
		{
			if (Settings.AcrylicEnabled == value)
				return;

			Settings.AcrylicEnabled = value;
			AcrylicEnabledChanged?.Invoke(value);
		}
	}

	public string DefaultInstallLocation
	{
		get => Settings.DefaultInstallLocation ?? _defaultInstallLocation;
		set { Settings.DefaultInstallLocation = value; }
	}

	public bool CloudSaveAutoSync
	{
		get => Settings.CloudSaveAutoSync;
		set => Settings.CloudSaveAutoSync = value;
	}

	// Unknown saved values fall back to exiting.
	public CloseBehavior CloseBehavior
	{
		get => Enum.IsDefined(Settings.CloseBehavior) ? Settings.CloseBehavior : CloseBehavior.Exit;
		set
		{
			if (!Enum.IsDefined(value) || Settings.CloseBehavior == value) return;
			Settings.CloseBehavior = value;
			CloseBehaviorChanged?.Invoke(value);
		}
	}

	// Unknown saved values fall back to not starting.
	public StartupBehavior StartupBehavior
	{
		get => Enum.IsDefined(Settings.StartupBehavior) ? Settings.StartupBehavior : StartupBehavior.None;
		set
		{
			if (Enum.IsDefined(value))
				Settings.StartupBehavior = value;
		}
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
