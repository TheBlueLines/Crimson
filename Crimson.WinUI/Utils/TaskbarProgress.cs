using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Crimson.Utils;

internal sealed class TaskbarProgress
{
	private const int TBPF_NOPROGRESS = 0x0;
	private const int TBPF_INDETERMINATE = 0x1;
	private const int TBPF_NORMAL = 0x2;
	private const int TBPF_PAUSED = 0x8;

	[ComImport]
	[Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
	private class TaskbarListComObject
	{
	}

	[ComImport]
	[Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eea84")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface ITaskbarList3
	{
		void HrInit();

		void AddTab(IntPtr hwnd);

		void DeleteTab(IntPtr hwnd);

		void ActivateTab(IntPtr hwnd);

		void SetActiveAlt(IntPtr hwnd);

		void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

		void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);

		void SetProgressState(IntPtr hwnd, int flags);
	}

	private readonly IntPtr _windowHandle;
	private readonly ITaskbarList3 _taskbar;

	private TaskbarProgress(IntPtr windowHandle, ITaskbarList3 taskbar)
	{
		_windowHandle = windowHandle;
		_taskbar = taskbar;
	}

	public static TaskbarProgress? TryCreate(Window window)
	{
		try
		{
			var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
			if (windowHandle == IntPtr.Zero)
				return null;

			var taskbar = (ITaskbarList3)new TaskbarListComObject();
			taskbar.HrInit();
			taskbar.AddTab(windowHandle);
			return new TaskbarProgress(windowHandle, taskbar);
		}
		catch
		{
			return null;
		}
	}

	public void SetNormal(int percent) => Set(percent, TBPF_NORMAL);

	public void SetPaused(int percent) => Set(percent, TBPF_PAUSED);

	public void SetIndeterminate()
	{
		try
		{
			_taskbar.SetProgressState(_windowHandle, TBPF_INDETERMINATE);
		}
		catch
		{
		}
	}

	public void Clear()
	{
		try
		{
			_taskbar.SetProgressState(_windowHandle, TBPF_NOPROGRESS);
		}
		catch
		{
		}
	}

	private void Set(int percent, int state)
	{
		try
		{
			var completed = (ulong)Math.Clamp(percent, 0, 100);
			_taskbar.SetProgressState(_windowHandle, state);
			_taskbar.SetProgressValue(_windowHandle, completed, 100);
		}
		catch
		{
		}
	}
}
