using System;
using System.IO;

namespace Crimson.Core;

internal static class InstallPathPolicy
{
	public static string ResolveFile(string installRoot, string relativePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
		ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
		var normalized = relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
		if (Path.IsPathRooted(normalized) || normalized.Contains(':'))
			throw new InvalidDataException("A manifest file path must be relative to the installation folder.");

		return RequireFile(installRoot, Path.Combine(installRoot, normalized));
	}

	public static string RequireFile(string installRoot, string filePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
		var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
		var file = Path.GetFullPath(filePath);
		RequireContainedFile(root, file);
		RejectLinkedDescendants(root, file);
		return file;
	}

	private static void RequireContainedFile(string root, string file)
	{
		var relative = Path.GetRelativePath(root, file);
		if (relative is "." or ".." || Path.IsPathRooted(relative) ||
			relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
			throw new InvalidDataException("A manifest file path escaped the installation folder.");
	}

	private static void RejectLinkedDescendants(string root, string file)
	{
		var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		for (var path = file; !string.Equals(path, root, comparison); path = Path.GetDirectoryName(path)!)
		{
			if (IsReparsePoint(path))
				throw new InvalidDataException("Manifest files cannot traverse symbolic links or reparse points.");
		}
	}

	private static bool IsReparsePoint(string path)
	{
		try
		{
			return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
		}
		catch (FileNotFoundException)
		{
			return false;
		}
		catch (DirectoryNotFoundException)
		{
			return false;
		}
	}
}
