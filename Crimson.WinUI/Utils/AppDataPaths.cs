using System;
using System.IO;
using Windows.ApplicationModel;
using Windows.Storage;

namespace Crimson.Utils;

/// <summary>
/// Resolves where Crimson keeps its own data. Packaged builds use the package's
/// LocalState so Windows' Reset and uninstall clear it; unpackaged builds keep
/// using %LOCALAPPDATA%\Crimson.
/// </summary>
public static class AppDataPaths
{
	// Written into the legacy folder once its contents have been copied into the
	// package, so a later Reset does not pull the old data back in.
	private const string MigrationMarker = ".migrated-to-package";

	// Regenerated on demand, not worth copying.
	private static readonly string[] SkippedDirectories = ["cache", "logs", "webview2"];

	private static readonly Lazy<string> RootPath = new(ResolveRoot);

	public static string Root => RootPath.Value;

	private static string LegacyRoot => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"Crimson");

	private static string ResolveRoot()
	{
		if (!IsPackaged())
			return LegacyRoot;

		var root = ApplicationData.Current.LocalFolder.Path;
		MigrateLegacyData(LegacyRoot, root);
		return root;
	}

	private static void MigrateLegacyData(string source, string destination)
	{
		var marker = Path.Combine(source, MigrationMarker);
		if (!Directory.Exists(source) || File.Exists(marker))
			return;

		try
		{
			CopyDirectory(source, destination, isRoot: true);
			File.WriteAllText(marker, destination);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Logging is not set up yet. Leave the marker unwritten so the next
			// launch retries; files that already made it across are skipped.
		}
	}

	private static void CopyDirectory(string source, string destination, bool isRoot)
	{
		Directory.CreateDirectory(destination);

		foreach (var file in Directory.EnumerateFiles(source))
		{
			var target = Path.Combine(destination, Path.GetFileName(file));
			if (!File.Exists(target))
				File.Copy(file, target);
		}

		foreach (var directory in Directory.EnumerateDirectories(source))
		{
			var name = Path.GetFileName(directory);
			if (isRoot && Array.Exists(SkippedDirectories, skipped => skipped.Equals(name, StringComparison.OrdinalIgnoreCase)))
				continue;

			CopyDirectory(directory, Path.Combine(destination, name), isRoot: false);
		}
	}

	private static bool IsPackaged()
	{
		try
		{
			_ = Package.Current.Id.Name;
			return true;
		}
		catch (InvalidOperationException)
		{
			return false;
		}
	}
}
