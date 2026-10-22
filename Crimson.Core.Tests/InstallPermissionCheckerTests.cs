using Crimson.Utils;

namespace Crimson.Tests;

public sealed class InstallPermissionCheckerTests : IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(),
		$"crimson-permission-{Guid.NewGuid():N}");

	[Fact]
	public void WritableDirectoryPassesWithoutLeavingProbeFiles()
	{
		Directory.CreateDirectory(_root);
		var checker = new FileSystemInstallPermissionChecker();

		var result = checker.Check(_root);

		Assert.True(result.CanWrite);
		Assert.Null(result.ErrorType);
		Assert.Empty(Directory.EnumerateFiles(_root, ".crimson-write-probe-*"));
	}

	[Fact]
	public void MissingDirectoryIsRejectedWithoutCreatingIt()
	{
		var missingPath = Path.Combine(_root, "missing");
		var checker = new FileSystemInstallPermissionChecker();

		var result = checker.Check(missingPath);

		Assert.False(result.CanWrite);
		Assert.Equal(nameof(DirectoryNotFoundException), result.ErrorType);
		Assert.False(Directory.Exists(missingPath));
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
			Directory.Delete(_root, recursive: true);
	}
}
