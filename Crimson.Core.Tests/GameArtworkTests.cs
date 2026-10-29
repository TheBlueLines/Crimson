using Crimson.Models;

namespace Crimson.Tests;

public sealed class GameArtworkTests
{
	[Fact]
	public void BackgroundArtworkPrefersWideImage()
	{
		var game = GameWithImages(
			new KeyImage { Type = "DieselGameBoxTall", Url = "tall" },
			new KeyImage { Type = "DieselGameBox", Url = "box" },
			new KeyImage { Type = "DieselGameBoxWide", Url = "wide" });

		Assert.Equal("wide", GameArtwork.SelectBackgroundUrl(game));
	}

	[Fact]
	public void BackgroundArtworkFallsBackToBoxArt()
	{
		var game = GameWithImages(
			new KeyImage { Type = "DieselGameBoxTall", Url = "tall" },
			new KeyImage { Type = "DieselGameBox", Url = "box" });

		Assert.Equal("box", GameArtwork.SelectBackgroundUrl(game));
	}

	[Fact]
	public void BackgroundArtworkFallsBackToTallBoxArt()
	{
		var game = GameWithImages(new KeyImage { Type = "DieselGameBoxTall", Url = "tall" });

		Assert.Equal("tall", GameArtwork.SelectBackgroundUrl(game));
	}

	[Fact]
	public void MissingBackgroundArtworkProducesNull()
	{
		Assert.Null(GameArtwork.SelectBackgroundUrl(GameWithImages()));
		Assert.Null(GameArtwork.SelectBackgroundUrl(null));
	}

	private static Game GameWithImages(params KeyImage[] images) => new()
	{
		AppName = "test",
		AppTitle = "Test",
		AssetInfos = null!,
		Metadata = new Metadata { KeyImages = [.. images] }
	};
}
