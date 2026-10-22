using Crimson.Core;

namespace Crimson.Tests;

public sealed class ManifestPathSafetyTests
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "crimson-manifest-root");

	[Theory]
	[InlineData("../outside.bin")]
	[InlineData("..\\outside.bin")]
	[InlineData("folder/../../outside.bin")]
	[InlineData("/outside.bin")]
	[InlineData("\\\\server\\share\\file.bin")]
	[InlineData("C:\\outside.bin")]
	[InlineData("C:outside.bin")]
	[InlineData("file.bin:stream")]
	[InlineData(".")]
	[InlineData("folder/..")]
	public void RejectsFilesOutsideInstallationRoot(string filename)
	{
		Assert.Throws<InvalidDataException>(() => InstallPathPolicy.ResolveFile(_root, filename));
	}

	[Fact]
	public void RejectsJunctionBelowInstallRoot()
	{
		if (!OperatingSystem.IsWindows())
			return;

		var root = Path.Combine(Path.GetTempPath(), $"crimson-path-junction-{Guid.NewGuid():N}");
		var installation = Path.Combine(root, "game");
		var outside = Path.Combine(root, "outside-game");
		var link = Path.Combine(installation, "linked");
		Directory.CreateDirectory(installation);
		Directory.CreateDirectory(outside);
		try
		{
			using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
				FileName = "cmd.exe",
				Arguments = $"/c mklink /J \"{link}\" \"{outside}\"",
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			});
			Assert.NotNull(process);
			Assert.True(process.WaitForExit(5000));
			Assert.Equal(0, process.ExitCode);

			Assert.Throws<InvalidDataException>(() => InstallPathPolicy.ResolveFile(installation, "linked/file.bin"));
		}
		finally
		{
			if (Directory.Exists(link))
				Directory.Delete(link);
			Directory.Delete(root, recursive: true);
		}
	}

	[Theory]
	[InlineData("game.exe")]
	[InlineData("Binaries/game.exe")]
	[InlineData("Binaries\\game.exe")]
	[InlineData("Binaries/../game.exe")]
	public void ResolvesNormalGamePaths(string filename)
	{
		var resolved = InstallPathPolicy.ResolveFile(_root, filename);
		var expected = Path.GetFullPath(Path.Combine(_root,
			filename.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));

		Assert.Equal(expected, resolved);
		Assert.False(Path.GetRelativePath(_root, resolved).StartsWith("..", StringComparison.Ordinal));
	}
}
