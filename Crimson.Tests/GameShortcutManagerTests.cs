using System.Runtime.InteropServices;
using Crimson.Core;
using Crimson.Models;
using Crimson.Utils;
using Serilog;

namespace Crimson.Tests;

public sealed class GameShortcutManagerTests
{
	[Theory]
	[InlineData("Game/Name", "Game_Name.lnk")]
	[InlineData("...", "Epic Game.lnk")]
	public void ShortcutFileNameIsSafe(string title, string expected)
	{
		Assert.Equal(expected, GameShortcutNaming.GetShortcutFileName(title));
	}

	[Fact]
	public void IconFileNameDoesNotContainPathSeparators()
	{
		Assert.Equal("Game_Name.ico", GameShortcutNaming.GetIconFileName("Game/Name"));
	}

	[Fact]
	public async Task GameArtworkCanBeWrittenAsIcon()
	{
		const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
		var path = Path.Combine(Path.GetTempPath(), $"crimson-{Guid.NewGuid():N}.ico");
		await using var source = new NonSeekableReadStream(Convert.FromBase64String(png));

		try
		{
			await GameShortcutManager.CreateIconFileAsync(source, path);
			var header = await File.ReadAllBytesAsync(path);
			Assert.Equal([0, 0, 1, 0, 1, 0], header[..6]);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void ShellLinkCanBeCreated()
	{
		var path = Path.Combine(Path.GetTempPath(), $"crimson-{Guid.NewGuid():N}.lnk");
		var game = new Game
		{
			AppName = "TestGame",
			AppTitle = "Test Game",
			AssetInfos = null!,
			Metadata = null!
		};

		try
		{
			GameShortcutManager.CreateShellLink(path, game, "C:\\Icons\\TestGame.ico", packaged: true);
			Assert.True(File.Exists(path));

			var shellType = Type.GetTypeFromProgID("WScript.Shell");
			Assert.NotNull(shellType);
			dynamic shell = Activator.CreateInstance(shellType)!;
			try
			{
				dynamic shortcut = shell.CreateShortcut(path);
				try
				{
					Assert.Equal(GameShortcutManager.GetPackagedAliasPath(), (string)shortcut.TargetPath);
					Assert.Equal("--launch-game=TestGame", (string)shortcut.Arguments);
					Assert.StartsWith("C:\\Icons\\TestGame.ico", (string)shortcut.IconLocation);
				}
				finally
				{
					if (Marshal.IsComObject(shortcut))
						Marshal.FinalReleaseComObject(shortcut);
				}
			}
			finally
			{
				if (Marshal.IsComObject(shell))
					Marshal.FinalReleaseComObject(shell);
			}
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void ShortcutArtifactsCanBeRemoved()
	{
		var root = Path.Combine(Path.GetTempPath(), $"crimson-shortcuts-{Guid.NewGuid():N}");
		var desktopDirectory = Path.Combine(root, "desktop");
		var startMenuDirectory = Path.Combine(root, "start");
		var iconDirectory = Path.Combine(root, "icons");
		var game = new Game
		{
			AppName = "TestGame",
			AppTitle = "Test Game",
			AssetInfos = null!,
			Metadata = null!
		};
		var shortcutName = GameShortcutNaming.GetShortcutFileName(game.AppTitle);
		var desktopShortcut = Path.Combine(desktopDirectory, shortcutName);
		var startMenuShortcut = Path.Combine(startMenuDirectory, shortcutName);
		var iconPath = Path.Combine(
			iconDirectory,
			"games",
			GameShortcutNaming.GetIconFileName(game.AppName));
		var unrelatedShortcut = Path.Combine(startMenuDirectory, "Other Game.lnk");

		Directory.CreateDirectory(desktopDirectory);
		Directory.CreateDirectory(startMenuDirectory);
		Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
		GameShortcutManager.CreateShellLink(desktopShortcut, game, iconPath, packaged: true);
		GameShortcutManager.CreateShellLink(startMenuShortcut, game, iconPath, packaged: true);
		File.WriteAllText(iconPath, string.Empty);
		File.WriteAllText(unrelatedShortcut, string.Empty);

		using var client = new HttpClient();
		using var logger = new LoggerConfiguration().CreateLogger();
		var manager = new GameShortcutManager(
			client,
			logger,
			desktopDirectory,
			startMenuDirectory,
			iconDirectory);

		try
		{
			manager.Remove(game);

			Assert.False(File.Exists(desktopShortcut));
			Assert.False(File.Exists(startMenuShortcut));
			Assert.False(File.Exists(iconPath));
			Assert.True(File.Exists(unrelatedShortcut));
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	private sealed class NonSeekableReadStream(byte[] data) : Stream
	{
		private readonly MemoryStream _inner = new(data);

		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public override int Read(byte[] buffer, int offset, int count) =>
			_inner.Read(buffer, offset, count);

		public override ValueTask<int> ReadAsync(
			Memory<byte> buffer,
			CancellationToken cancellationToken = default) =>
			_inner.ReadAsync(buffer, cancellationToken);

		public override void Flush() => throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		protected override void Dispose(bool disposing)
		{
			if (disposing)
				_inner.Dispose();
			base.Dispose(disposing);
		}
	}
}
