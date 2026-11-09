using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Crimson.Models;

namespace Crimson.Core;

internal sealed record CloudSaveKnownFolders(
	string LocalAppData,
	string Documents,
	string UserProfile,
	string SavedGames)
{
	public static CloudSaveKnownFolders Current()
	{
		var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		return new CloudSaveKnownFolders(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
			profile,
			GetSavedGamesFolder() ?? Path.Combine(profile, "Saved Games"));
	}

	private static string? GetSavedGamesFolder()
	{
		if (!OperatingSystem.IsWindows())
			return null;

		var savedGames = new Guid("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4");
		if (SHGetKnownFolderPath(savedGames, 0, IntPtr.Zero, out var pointer) != 0)
			return null;

		try
		{
			return Marshal.PtrToStringUni(pointer);
		}
		finally
		{
			Marshal.FreeCoTaskMem(pointer);
		}
	}

	[DllImport("shell32.dll")]
	private static extern int SHGetKnownFolderPath(
		[MarshalAs(UnmanagedType.LPStruct)] Guid folderId,
		uint flags,
		IntPtr token,
		out IntPtr path);
}

internal sealed class CloudSaveFilter
{
	private readonly IReadOnlyList<string> _include;
	private readonly IReadOnlyList<string> _exclude;

	public CloudSaveFilter(string? include, string? exclude)
	{
		_include = Parse(include);
		_exclude = Parse(exclude);
	}

	public bool HasIncludeList => _include.Count > 0;

	public bool Includes(string relativePath) =>
		(_include.Count == 0 || Matches(_include, relativePath)) && !Matches(_exclude, relativePath);

	private static List<string> Parse(string? value) => (value ?? string.Empty)
		.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
		.Select(pattern => pattern.Replace('\\', '/'))
		.ToList();

	private static bool Matches(IReadOnlyList<string> patterns, string path) => patterns.Any(pattern =>
		pattern.EndsWith('/')
			? path.StartsWith(pattern, StringComparison.OrdinalIgnoreCase)
			: path.EndsWith(pattern, StringComparison.OrdinalIgnoreCase) ||
			  FileSystemName.MatchesSimpleExpression(pattern, path, ignoreCase: true));
}

internal sealed record CloudSaveLocation(string Folder, string Template, CloudSaveFilter Filter)
{
	private static readonly Regex Token = new(@"\{[^{}]*\}", RegexOptions.CultureInvariant | RegexOptions.Compiled);

	/// <summary>
	/// Resolves a game's save location, or returns null when it has none Crimson can safely manage.
	/// The template comes from remote metadata and decides where downloads are written, so it must
	/// start at a known root and may not climb out of it. A bare root such as Documents is only
	/// accepted with an include list, because a sync replaces everything in scope.
	/// </summary>
	public static CloudSaveLocation? TryResolve(Game game, string accountId, CloudSaveKnownFolders folders)
	{
		var attributes = game.Metadata?.CustomAttributes;
		var template = attributes?.CloudSaveFolder?.Value;
		var installPath = game.LocalAppState?.InstallPath;
		if (string.IsNullOrWhiteSpace(template) || string.IsNullOrWhiteSpace(installPath) || game.IsDlc() ||
			game.LocalAppState?.InstallStatus is not (InstallState.Installed or InstallState.NeedUpdate))
			return null;

		var filter = new CloudSaveFilter(attributes!.CloudIncludeList?.Value, attributes.CloudExcludeList?.Value);
		var segments = template.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (segments.Length == 0 || (segments.Length == 1 && !filter.HasIncludeList))
			return null;

		var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["{appdata}"] = folders.LocalAppData,
			["{installdir}"] = installPath,
			["{userdir}"] = folders.Documents,
			["{userprofile}"] = folders.UserProfile,
			["{usersavedgames}"] = folders.SavedGames
		};
		if (!roots.TryGetValue(segments[0], out var root) || string.IsNullOrWhiteSpace(root))
			return null;

		var path = root;
		foreach (var segment in segments.Skip(1))
		{
			var expanded = Token.Replace(segment, match =>
				match.Value.Equals("{epicid}", StringComparison.OrdinalIgnoreCase) ? accountId : "\0");
			if (expanded is "." or ".." || expanded.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
				expanded.Contains(':'))
				return null;
			path = Path.Combine(path, expanded);
		}

		return new CloudSaveLocation(Path.GetFullPath(path), template, filter);
	}
}
