using System;
using System.IO;
using Crimson.Core;

namespace Crimson.Utils;

public sealed class FileSystemInstallPermissionChecker : IInstallPermissionChecker
{
	private const string ProbePrefix = ".crimson-write-probe-";

	public InstallPermissionCheckResult Check(string folderPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
		var root = Path.GetFullPath(folderPath);
		if (!Directory.Exists(root))
			return new InstallPermissionCheckResult(false, nameof(DirectoryNotFoundException));

		var probePath = Path.Combine(root, $"{ProbePrefix}{Guid.NewGuid():N}.tmp");
		try
		{
			using (var stream = new FileStream(
					   probePath,
					   FileMode.CreateNew,
					   FileAccess.Write,
					   FileShare.None))
			{
				stream.WriteByte(0x43);
				stream.Flush(flushToDisk: true);
			}

			File.Delete(probePath);
			return new InstallPermissionCheckResult(true);
		}
		catch (Exception exception) when (exception is
				   IOException or
				   UnauthorizedAccessException or
				   NotSupportedException)
		{
			return new InstallPermissionCheckResult(
				false,
				exception.GetType().Name,
				TryDeleteProbe(probePath));
		}
	}

	private static string? TryDeleteProbe(string path)
	{
		if (!File.Exists(path))
			return null;

		try
		{
			File.Delete(path);
			return File.Exists(path) ? "FileStillExists" : null;
		}
		catch (Exception exception) when (exception is
				   IOException or
				   UnauthorizedAccessException or
				   NotSupportedException)
		{
			return exception.GetType().Name;
		}
	}
}
