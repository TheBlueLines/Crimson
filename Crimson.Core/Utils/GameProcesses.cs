using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Crimson.Utils;

/// <summary>
/// Locates and stops the processes that belong to a running game. Launchers such as Fortnite's
/// start the real game client (and anti-cheat bootstrappers) as separate processes, so the
/// process Crimson started is not the only one that has to close.
/// </summary>
internal static class GameProcesses
{
	private const uint ProcessQueryLimitedInformation = 0x1000;

	/// <summary>
	/// Returns the launched process plus every running process whose executable lives inside the
	/// game's install folder. The caller owns and must dispose the returned processes.
	/// </summary>
	public static List<Process> Find(int rootId, string? installPath)
	{
		var installRoot = NormalizeDirectory(installPath);
		var result = new List<Process>();
		foreach (var process in Process.GetProcesses())
		{
			if (process.Id == rootId ||
				(installRoot is not null && IsInside(GetImagePath(process.Id), installRoot)))
				result.Add(process);
			else
				process.Dispose();
		}

		return result;
	}

	/// <summary>Asks a process to close its main window. Returns true when the request was delivered.</summary>
	public static bool RequestClose(Process process)
	{
		try
		{
			return !process.HasExited && process.CloseMainWindow();
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			return false;
		}
	}

	public static void Kill(Process process)
	{
		try
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
									   or NotSupportedException)
		{
			// Already gone, or not ours to terminate.
		}
	}

	/// <summary>Waits until every process has exited. Returns false when the timeout elapses first.</summary>
	public static async Task<bool> WaitForExitAsync(IReadOnlyList<Process> processes, TimeSpan timeout)
	{
		try
		{
			await Task.WhenAll(processes.Select(WaitForExitSafeAsync)).WaitAsync(timeout);
			return true;
		}
		catch (TimeoutException)
		{
			return false;
		}
	}

	private static async Task WaitForExitSafeAsync(Process process)
	{
		try
		{
			await process.WaitForExitAsync();
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			// The process is gone or cannot be observed; nothing left to wait for.
		}
	}

	private static string? NormalizeDirectory(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return null;

		try
		{
			return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return null;
		}
	}

	private static bool IsInside(string? file, string directory) =>
		file is not null && file.StartsWith(directory, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Reads a process's executable path with the minimal query right, which also works for games
	/// that deny the broader access <see cref="Process.MainModule"/> needs.
	/// </summary>
	private static string? GetImagePath(int processId)
	{
		if (!OperatingSystem.IsWindows())
			return null;

		var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
		if (handle == IntPtr.Zero)
			return null;

		try
		{
			var buffer = new StringBuilder(1024);
			var size = buffer.Capacity;
			return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
		}
		finally
		{
			CloseHandle(handle);
		}
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
	private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr handle);
}
