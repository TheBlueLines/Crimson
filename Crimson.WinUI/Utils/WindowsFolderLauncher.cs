using System;
using System.Diagnostics;
using Crimson.Core;

namespace Crimson.Utils;

public sealed class WindowsFolderLauncher : IFolderLauncher
{
	public void Open(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		Process.Start(new ProcessStartInfo
		{
			UseShellExecute = true,
			FileName = path,
		});
	}
}
