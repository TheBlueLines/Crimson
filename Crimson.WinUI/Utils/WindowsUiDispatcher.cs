using System;
using Crimson.Core;
using Microsoft.UI.Dispatching;

namespace Crimson.Utils;

public sealed class WindowsUiDispatcher : IUiDispatcher
{
	private readonly DispatcherQueue _dispatcherQueue;

	public WindowsUiDispatcher(DispatcherQueue dispatcherQueue)
	{
		_dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
	}

	public bool TryEnqueue(Action callback)
	{
		ArgumentNullException.ThrowIfNull(callback);
		return _dispatcherQueue.TryEnqueue(() => callback());
	}
}
