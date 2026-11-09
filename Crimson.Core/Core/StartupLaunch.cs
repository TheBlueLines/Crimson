using System;
using System.Linq;

namespace Crimson.Core;

/// <summary>
/// Recognizes the command line used when Windows starts an unpackaged Crimson at sign-in.
/// </summary>
public static class StartupLaunch
{
	public const string CommandLineArgument = "--startup";

	public static bool IsStartupCommandLine(string arguments) =>
		arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(CommandLineArgument);
}
