using Crimson.Utils;

namespace Crimson.Tests;

public sealed class FileSystemVolumeResolverTests
{
	[Fact]
	public void ResolvesVolumeContainingTemporaryDirectory()
	{
		var volume = new FileSystemVolumeResolver().GetVolume(Path.GetTempPath());

		Assert.True(volume.IsReady);
	}

	[Fact]
	public void SelectsLongestContainingMountRoot()
	{
		var root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;
		var mount = Path.Combine(root, "mnt", "games");
		var nestedMount = Path.Combine(mount, "external");
		var target = Path.Combine(nestedMount, "Crimson", "Game");

		var selected = FileSystemVolumeResolver.SelectVolumeRoot(target, [root, mount, nestedMount]);

		Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(nestedMount)), selected);
	}

	[Fact]
	public void DoesNotTreatTextualPrefixAsAncestor()
	{
		var root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;
		var falsePrefix = Path.Combine(root, "mnt", "game");
		var actualMount = Path.Combine(root, "mnt", "games");
		var target = Path.Combine(actualMount, "Crimson");

		var selected = FileSystemVolumeResolver.SelectVolumeRoot(target, [root, falsePrefix, actualMount]);

		Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(actualMount)), selected);
	}

	[Fact]
	public void MissingContainingVolumeIsRejected()
	{
		var temp = Path.GetFullPath(Path.GetTempPath());
		var target = Path.Combine(temp, "target", "game");
		var unrelated = Path.Combine(temp, "unrelated");

		Assert.Throws<DriveNotFoundException>(() =>
			FileSystemVolumeResolver.SelectVolumeRoot(target, [unrelated]));
	}
}
