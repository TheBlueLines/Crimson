using System;

namespace Crimson.Core;

public interface IUiDispatcher
{
	bool TryEnqueue(Action callback);
}
