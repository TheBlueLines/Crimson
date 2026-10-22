using System;
using System.IO;
using System.Linq;

namespace Crimson.Core;

public static class GameShortcutNaming
{
	public static string GetShortcutFileName(string title)
	{
		var sanitized = string.Concat(title.Select(character =>
			Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim(' ', '.');
		if (string.IsNullOrWhiteSpace(sanitized))
			sanitized = "Epic Game";
		if (sanitized.Length > 120)
			sanitized = sanitized[..120].TrimEnd(' ', '.');
		return $"{sanitized}.lnk";
	}

	public static string GetIconFileName(string appName)
	{
		var sanitized = string.Concat(appName.Select(character =>
			char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
				? character
				: '_'));
		return (string.IsNullOrEmpty(sanitized) ? "game" : sanitized) + ".ico";
	}
}
