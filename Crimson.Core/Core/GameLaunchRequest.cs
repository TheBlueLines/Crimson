using System;

namespace Crimson.Core;

public static class GameLaunchRequest
{
	public const string ProtocolScheme = "crimson";
	private const string LaunchArgumentPrefix = "--launch-game=";

	public static Uri CreateProtocolUri(string appName) =>
		new($"{ProtocolScheme}://launch?app={Uri.EscapeDataString(appName)}");

	public static string CreateCommandLineArgument(string appName) =>
		LaunchArgumentPrefix + Uri.EscapeDataString(appName);

	internal static bool TryParse(Uri uri, out string appName)
	{
		appName = string.Empty;
		if (!uri.Scheme.Equals(ProtocolScheme, StringComparison.OrdinalIgnoreCase) ||
			!uri.Host.Equals("launch", StringComparison.OrdinalIgnoreCase))
			return false;

		foreach (var parameter in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
		{
			var parts = parameter.Split('=', 2);
			if (parts.Length != 2 || !Uri.UnescapeDataString(parts[0]).Equals("app", StringComparison.Ordinal))
				continue;

			var value = Uri.UnescapeDataString(parts[1]);
			if (IsValidAppName(value))
			{
				appName = value;
				return true;
			}
		}

		return false;
	}

	internal static bool TryParseCommandLine(string arguments, out string appName)
	{
		appName = string.Empty;
		foreach (var argument in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
		{
			if (!argument.StartsWith(LaunchArgumentPrefix, StringComparison.Ordinal))
				continue;

			var value = Uri.UnescapeDataString(argument[LaunchArgumentPrefix.Length..]);
			if (IsValidAppName(value))
			{
				appName = value;
				return true;
			}
		}

		return false;
	}

	private static bool IsValidAppName(string value) =>
		value.Length is > 0 and <= 256 &&
		value.IndexOfAny(['\r', '\n', '\0']) < 0;
}
