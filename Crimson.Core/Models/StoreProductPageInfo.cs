using System;
using System.Collections.Generic;

namespace Crimson.Models;

/// <summary>
/// What Epic's web product page shows beyond the catalog offer: the media gallery, the full
/// description, system requirements, languages, ratings and add-ons. Every part is optional;
/// Epic fills them in per product, and a part that failed to load is simply empty.
/// </summary>
public sealed class StoreProductPageInfo
{
	public static readonly StoreProductPageInfo Empty = new();

	public IReadOnlyList<StoreMediaItem> Media { get; init; } = [];
	public string? LongDescription { get; init; }
	public IReadOnlyList<StoreRequirement> Requirements { get; init; } = [];
	public IReadOnlyList<string> AudioLanguages { get; init; } = [];
	public IReadOnlyList<string> TextLanguages { get; init; } = [];
	public IReadOnlyList<StoreSocialLink> SocialLinks { get; init; } = [];
	public StorePlayerRating? PlayerRating { get; init; }
	public StoreCriticRating? CriticRating { get; init; }
	public IReadOnlyList<StoreSearchResult> AddOns { get; init; } = [];
}

/// <summary>A gallery entry. Videos only expose a cover picture through the catalog.</summary>
public sealed record StoreMediaItem(string ImageUrl, bool IsVideo);

/// <summary>One row of the Windows system requirements table.</summary>
public sealed record StoreRequirement(string Title, string Minimum, string Recommended);

public sealed record StoreSocialLink(string Platform, Uri Url);

/// <summary>Epic player ratings: the average out of 5 and the poll results players voted for.</summary>
public sealed record StorePlayerRating(double Average, IReadOnlyList<StoreRatingHighlight> Highlights);

/// <summary>A poll result such as "This game has" / "Great Boss Battles".</summary>
public sealed record StoreRatingHighlight(string Prefix, string Title);

public sealed record StoreCriticRating(int? Score, int? PercentRecommended, string? Award);
