using System;
using System.Threading.Tasks;
using Crimson.Core;
using Microsoft.Win32;
using Serilog;
using Windows.ApplicationModel;

namespace Crimson.Utils;

/// <summary>
/// MSIX installs use the startup task declared in Package.appxmanifest, because registry writes
/// from a package are private to it. The ZIP release has no package and uses the Run key.
/// </summary>
public sealed class WindowsStartupRegistration : IStartupRegistration
{
	private const string TaskId = "CrimsonStartup";
	private const string ValueName = "Crimson";
	private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
	private const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

	private readonly ILogger _logger;

	public WindowsStartupRegistration(ILogger logger)
	{
		_logger = logger;
	}

	public async Task<bool> SetEnabledAsync(bool enabled)
	{
		try
		{
			return AppDataPaths.IsPackaged()
				? await SetPackagedEnabledAsync(enabled)
				: await Task.Run(() => SetRunValue(enabled));
		}
		catch (Exception ex)
		{
			_logger.Warning(ex, "Could not change whether Crimson starts with Windows");
			return false;
		}
	}

	public async Task<bool?> IsEnabledAsync()
	{
		try
		{
			return AppDataPaths.IsPackaged()
				? IsEnabledState((await StartupTask.GetAsync(TaskId)).State)
				: await Task.Run(IsRunValueActive);
		}
		catch (Exception ex)
		{
			_logger.Warning(ex, "Could not read whether Crimson starts with Windows");
			return null;
		}
	}

	private static async Task<bool> SetPackagedEnabledAsync(bool enabled)
	{
		var task = await StartupTask.GetAsync(TaskId);
		if (!enabled)
		{
			task.Disable();
			return true;
		}

		return IsEnabledState(await task.RequestEnableAsync());
	}

	private static bool IsEnabledState(StartupTaskState state) =>
		state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;

	private static bool SetRunValue(bool enabled)
	{
		using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath);
		if (!enabled)
		{
			runKey.DeleteValue(ValueName, throwOnMissingValue: false);
			return true;
		}

		if (GetRunCommand() is not { } command)
			return false;

		runKey.SetValue(ValueName, command);
		return !IsTurnedOffInWindows();
	}

	/// <summary>
	/// False also when the entry points at another copy, such as after the ZIP was moved.
	/// </summary>
	private static bool IsRunValueActive()
	{
		using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath);
		var command = runKey?.GetValue(ValueName) as string;
		return command is not null && command == GetRunCommand() && !IsTurnedOffInWindows();
	}

	/// <summary>
	/// The Windows startup apps list leaves the Run value alone and records its toggle here.
	/// An odd first byte means the user turned the entry off.
	/// </summary>
	private static bool IsTurnedOffInWindows()
	{
		using var approvedKey = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath);
		return approvedKey?.GetValue(ValueName) is byte[] { Length: > 0 } state && (state[0] & 1) == 1;
	}

	private static string? GetRunCommand() =>
		Environment.ProcessPath is { } path ? $"\"{path}\" {StartupLaunch.CommandLineArgument}" : null;
}
