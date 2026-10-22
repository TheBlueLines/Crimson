using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Crimson;

public static class Program
{
	private static IntPtr _redirectEventHandle;

	[STAThread]
	private static int Main()
	{
		WinRT.ComWrappersSupport.InitializeComWrappers();
		var activationArguments = AppInstance.GetCurrent().GetActivatedEventArgs();
		var mainInstance = AppInstance.FindOrRegisterForKey("Crimson");

		if (!mainInstance.IsCurrent)
		{
			RedirectActivation(activationArguments, mainInstance);
			return 0;
		}

		App.InitialActivationArguments = activationArguments;
		mainInstance.Activated += (_, redirectedArguments) =>
			App.RouteActivation(redirectedArguments);

		Application.Start(_ =>
		{
			var dispatcher = DispatcherQueue.GetForCurrentThread();
			SynchronizationContext.SetSynchronizationContext(
				new DispatcherQueueSynchronizationContext(dispatcher));
			new App();
		});
		return 0;
	}

	private static void RedirectActivation(
		AppActivationArguments activationArguments,
		AppInstance mainInstance)
	{
		_redirectEventHandle = CreateEvent(IntPtr.Zero, true, false, null);
		Task.Run(async () =>
		{
			try
			{
				await mainInstance.RedirectActivationToAsync(activationArguments);
			}
			finally
			{
				SetEvent(_redirectEventHandle);
			}
		});

		_ = CoWaitForMultipleObjects(0, uint.MaxValue, 1, [_redirectEventHandle], out _);
		CloseHandle(_redirectEventHandle);
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr CreateEvent(
		IntPtr eventAttributes,
		bool manualReset,
		bool initialState,
		string? name);

	[DllImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetEvent(IntPtr handle);

	[DllImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(IntPtr handle);

	[DllImport("ole32.dll")]
	private static extern uint CoWaitForMultipleObjects(
		uint flags,
		uint milliseconds,
		ulong handleCount,
		IntPtr[] handles,
		out uint index);
}
