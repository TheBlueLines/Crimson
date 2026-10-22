using Crimson.Core;
using Crimson.Models;
using Serilog;

namespace Crimson.Tests;

public sealed class ShortcutOwnershipTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"crimson-shortcut-ownership-{Guid.NewGuid():N}");
	private readonly HttpClient _http = new();
	private readonly Serilog.Core.Logger _logger = new LoggerConfiguration().CreateLogger();

	[Fact]
	public void RemovePreservesUnrelatedSameNameFile()
	{
		var manager = CreateManager();
		var game = CreateGame("owned");
		var path = GetDesktopPath(game);
		File.WriteAllText(path, "This file was not created by Crimson.");

		manager.Remove(game);

		Assert.Equal("This file was not created by Crimson.", File.ReadAllText(path));
	}

	[Fact]
	public void RemovePreservesOtherGameWithSameTitle()
	{
		var manager = CreateManager();
		var existingGame = CreateGame("existing");
		var path = GetDesktopPath(existingGame);
		GameShortcutManager.CreateShellLink(path, existingGame, string.Empty, packaged: true);
		var original = File.ReadAllBytes(path);

		manager.Remove(CreateGame("new"));

		Assert.Equal(original, File.ReadAllBytes(path));
	}

	[Fact]
	public void CreationDoesNotOverwriteOtherGameWithSameTitle()
	{
		CreateManager();
		var game = CreateGame("existing");
		var path = GetDesktopPath(game);
		GameShortcutManager.CreateShellLink(path, game, string.Empty, packaged: true);
		var original = File.ReadAllBytes(path);

		Assert.Throws<IOException>(() =>
			GameShortcutManager.CreateShellLink(path, CreateGame("new"), string.Empty, packaged: true));

		Assert.Equal(original, File.ReadAllBytes(path));
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void RemoveDeletesMatchingCrimsonShortcut(bool packaged)
	{
		var manager = CreateManager();
		var game = CreateGame("owned");
		var path = GetDesktopPath(game);
		GameShortcutManager.CreateShellLink(path, game, string.Empty, packaged);

		manager.Remove(game);

		Assert.False(File.Exists(path));
	}

	private GameShortcutManager CreateManager()
	{
		var desktop = Path.Combine(_root, "desktop");
		Directory.CreateDirectory(desktop);
		return new GameShortcutManager(_http, _logger, desktop,
			Path.Combine(_root, "start"), Path.Combine(_root, "icons"));
	}

	private string GetDesktopPath(Game game) =>
		Path.Combine(_root, "desktop", GameShortcutNaming.GetShortcutFileName(game.AppTitle));

	private static Game CreateGame(string appName) => new()
	{
		AppName = appName,
		AppTitle = "Shared Game Title",
		Metadata = null!,
		AssetInfos = null!
	};

	public void Dispose()
	{
		_http.Dispose();
		_logger.Dispose();
		if (Directory.Exists(_root))
			Directory.Delete(_root, recursive: true);
	}
}
