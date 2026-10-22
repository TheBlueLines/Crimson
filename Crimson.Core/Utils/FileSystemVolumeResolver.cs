using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Crimson.Utils;

public interface IFileSystemVolumeResolver
{
	DriveInfo GetVolume(string path);
	bool AreOnSameVolume(string firstPath, string secondPath);
}

public sealed class FileSystemVolumeResolver : IFileSystemVolumeResolver
{
	public DriveInfo GetVolume(string path)
	{
		var drives = DriveInfo.GetDrives();
		var root = ResolveVolumeRoot(path, drives.Select(drive => drive.RootDirectory.FullName));
		var comparison = GetPathComparison();
		var volume = drives.FirstOrDefault(drive =>
			string.Equals(
				NormalizeRoot(drive.RootDirectory.FullName),
				root,
				comparison)) ?? new DriveInfo(root);

		if (!volume.IsReady)
			throw new IOException($"Volume {volume.Name} is not ready.");

		return volume;
	}

	public bool AreOnSameVolume(string firstPath, string secondPath)
	{
		var roots = DriveInfo.GetDrives()
			.Select(drive => drive.RootDirectory.FullName)
			.ToArray();
		var comparison = GetPathComparison();
		return string.Equals(
			ResolveVolumeRoot(firstPath, roots),
			ResolveVolumeRoot(secondPath, roots),
			comparison);
	}

	private static string ResolveVolumeRoot(string path, IEnumerable<string> volumeRoots)
	{
		try
		{
			return SelectVolumeRoot(path, volumeRoots);
		}
		catch (DriveNotFoundException) when (OperatingSystem.IsWindows())
		{
			var root = Path.GetPathRoot(Path.GetFullPath(path));
			if (string.IsNullOrEmpty(root))
				throw;

			return NormalizeRoot(root);
		}
	}

	internal static string SelectVolumeRoot(string path, IEnumerable<string> volumeRoots)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(volumeRoots);

		var fullPath = Path.GetFullPath(path);
		var root = volumeRoots
			.Select(NormalizeRoot)
			.Where(candidate => ContainsPath(candidate, fullPath))
			.OrderByDescending(candidate => candidate.Length)
			.FirstOrDefault();

		return root ?? throw new DriveNotFoundException($"No mounted volume contains path '{fullPath}'.");
	}

	private static bool ContainsPath(string root, string path)
	{
		var relative = Path.GetRelativePath(root, path);
		return relative == "." ||
			(!Path.IsPathRooted(relative) &&
			 relative != ".." &&
			 !relative.StartsWith($"..{Path.DirectorySeparatorChar}", GetPathComparison()));
	}

	private static string NormalizeRoot(string path) =>
		Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

	private static StringComparison GetPathComparison() =>
		OperatingSystem.IsWindows()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
}
