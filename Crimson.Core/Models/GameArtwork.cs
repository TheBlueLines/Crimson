using System;
using System.Linq;

namespace Crimson.Models;

/// <summary>
/// Picks the artwork used by the shell. The launcher background wants the widest
/// image available so the whole key art fits a desktop window without a hard crop.
/// </summary>
public static class GameArtwork
{
	private static readonly string[] WideImageTypes =
	[
		"DieselGameBoxWide",
		"DieselStoreFrontWide",
		"OfferImageWide",
		"featuredMedia"
	];

	private static readonly string[] FallbackImageTypes =
	[
		"DieselGameBox",
		"DieselGameBoxTall",
		"DieselStoreFrontTall",
		"OfferImageTall",
		"Thumbnail"
	];

	public static string? SelectBackgroundUrl(Game? game)
	{
		var images = game?.Metadata?.KeyImages;
		if (images is null || images.Count == 0)
			return null;

		foreach (var imageType in WideImageTypes.Concat(FallbackImageTypes))
		{
			var url = images
				.FirstOrDefault(image =>
					string.Equals(image.Type, imageType, StringComparison.OrdinalIgnoreCase))?.Url;
			if (!string.IsNullOrWhiteSpace(url))
				return url;
		}

		return images.FirstOrDefault(image => !string.IsNullOrWhiteSpace(image.Url))?.Url;
	}
}
