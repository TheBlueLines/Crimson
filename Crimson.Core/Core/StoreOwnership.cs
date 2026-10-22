using System;
using System.Collections.Generic;
using System.Linq;
using Crimson.Models;

namespace Crimson.Core;

public enum StoreOwnershipState
{
	NotOwned,
	Owned,
	Installed
}

/// <summary>
/// Matches store offers against the owned library. Offers list the catalog items they
/// grant, which are the same ids the library assets carry.
/// </summary>
public static class StoreOwnership
{
	private const string BaseGameOfferType = "BASE_GAME";
	private const string EditionOfferType = "EDITION";

	public static Game? FindOwnedGame(IEnumerable<Game> library, StoreSearchResult offer)
	{
		var games = library as IReadOnlyCollection<Game> ?? library.ToList();
		if (offer.CatalogItemIds.Count > 0)
		{
			var owned = games
				.Where(game => offer.CatalogItemIds.Contains(
					game.AssetInfos?.Windows?.CatalogItemId ?? string.Empty,
					StringComparer.OrdinalIgnoreCase))
				.ToList();

			// Bundles and editions grant several items; owning the base game alone does not
			// mean owning the upgrade, so every granted item has to be in the library.
			if (IsMultiItemOffer(offer) && owned.Count < offer.CatalogItemIds.Count)
				return null;

			if (owned.Count > 0)
				return owned.FirstOrDefault(game => game.Metadata is not null && !game.IsDlc()) ?? owned[0];
		}

		// DLC pages share the base game's slug, so the slug fallback is only safe for base games.
		if (!string.Equals(offer.OfferType, BaseGameOfferType, StringComparison.OrdinalIgnoreCase) ||
			string.IsNullOrWhiteSpace(offer.ProductSlug))
			return null;

		return games.FirstOrDefault(game =>
			game.Metadata is not null &&
			!game.IsDlc() &&
			string.Equals(
				NormalizeSlug(game.Metadata.CustomAttributes?.ComEpicgamesAppProductSlug?.Value),
				offer.ProductSlug,
				StringComparison.OrdinalIgnoreCase));
	}

	// Library metadata stores slugs like "hades/home"; store pages use the first segment.
	private static string? NormalizeSlug(string? slug)
	{
		var trimmed = slug?.Trim().Trim('/');
		if (string.IsNullOrEmpty(trimmed))
			return null;

		var separator = trimmed.IndexOf('/');
		return separator >= 0 ? trimmed[..separator] : trimmed;
	}

	public static StoreOwnershipState GetState(Game? game)
	{
		if (game is null)
			return StoreOwnershipState.NotOwned;

		return game.LocalAppState?.InstallStatus is InstallState.Installed or InstallState.Playing or InstallState.NeedUpdate
			? StoreOwnershipState.Installed
			: StoreOwnershipState.Owned;
	}

	private static bool IsMultiItemOffer(StoreSearchResult offer) =>
		offer.CatalogItemIds.Count > 1 &&
		(string.Equals(offer.OfferType, StoreSearchResult.BundleOfferType, StringComparison.OrdinalIgnoreCase) ||
		 string.Equals(offer.OfferType, EditionOfferType, StringComparison.OrdinalIgnoreCase));
}
