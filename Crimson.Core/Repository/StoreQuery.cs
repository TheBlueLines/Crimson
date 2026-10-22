using System;
using System.Collections.Generic;
using System.Globalization;

namespace Crimson.Repository;

public enum StoreSortOrder
{
	Relevance,
	NewReleases,
	PriceLowToHigh,
	PriceHighToLow,
	Title
}

public sealed record StoreQuery
{
	// Games, bundles, editions and software; leaves add-ons out of browsing.
	internal const string BrowseCategory = "games/edition/base|bundles/games|editors|software/edition/base";

	public string? Keywords { get; init; }
	public StoreSortOrder SortOrder { get; init; } = StoreSortOrder.Relevance;
	public bool OnSaleOnly { get; init; }
	public int Start { get; init; }
	public int Count { get; init; } = 40;

	public bool HasKeywords => !string.IsNullOrWhiteSpace(Keywords);

	internal Dictionary<string, object?> ToVariables(StoreRegion region, DateTimeOffset now)
	{
		var (sortBy, sortDir) = SortOrder switch
		{
			StoreSortOrder.NewReleases => ("releaseDate", "DESC"),
			StoreSortOrder.PriceLowToHigh => ("currentPrice", "ASC"),
			StoreSortOrder.PriceHighToLow => ("currentPrice", "DESC"),
			StoreSortOrder.Title => ("title", "ASC"),
			_ => HasKeywords ? ("relevancy", "DESC") : ("releaseDate", "DESC")
		};

		var variables = new Dictionary<string, object?>
		{
			["count"] = Math.Clamp(Count, 1, 100),
			["start"] = Math.Max(0, Start),
			["country"] = region.Country,
			["locale"] = region.Locale,
			["sortBy"] = sortBy,
			["sortDir"] = sortDir
		};

		if (HasKeywords)
			variables["keywords"] = Keywords!.Trim();
		else
			variables["category"] = BrowseCategory;

		if (OnSaleOnly)
			variables["onSale"] = true;

		// Without an upper bound, sorting by release date lists far-future placeholders first.
		if (sortBy == "releaseDate")
			variables["effectiveDate"] = $"[,{now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)}]";

		return variables;
	}
}

public sealed record StoreRegion(string Country, string Locale)
{
	public static readonly StoreRegion Default = new("US", "en-US");

	public static StoreRegion Current =>
		From(RegionInfo.CurrentRegion.TwoLetterISORegionName, CultureInfo.CurrentUICulture.Name);

	/// <summary>
	/// Epic prices by country. Regions without a two-letter code (such as "World") fall back to US.
	/// </summary>
	public static StoreRegion From(string? countryCode, string? localeName)
	{
		var country = countryCode;
		if (country is not { Length: 2 } || !char.IsAsciiLetter(country[0]) || !char.IsAsciiLetter(country[1]))
			country = Default.Country;

		var locale = string.IsNullOrWhiteSpace(localeName) ? Default.Locale : localeName;
		return new StoreRegion(country.ToUpperInvariant(), locale);
	}
}
