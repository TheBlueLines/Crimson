using System;
using System.Collections.Generic;

namespace Crimson.Models;

/// <summary>
/// One Epic Games Store offer as returned by the catalog search.
/// </summary>
public sealed class StoreSearchResult
{
	public const string BundleOfferType = "BUNDLE";

	public required string Title { get; init; }
	public string? ProductSlug { get; init; }
	public string? Namespace { get; init; }
	public string? OfferId { get; init; }
	public string? OfferType { get; init; }
	public string? Description { get; init; }
	public string? Seller { get; init; }
	public string? ImageUrl { get; init; }
	public string? WideImageUrl { get; init; }
	public DateTimeOffset? EffectiveDate { get; init; }
	public StorePrice? Price { get; init; }
	public StorePromotion? CurrentPromotion { get; init; }
	public StorePromotion? UpcomingPromotion { get; init; }
	public IReadOnlyList<string> CatalogItemIds { get; init; } = [];

	public bool HasOfferReference => !string.IsNullOrWhiteSpace(Namespace) && !string.IsNullOrWhiteSpace(OfferId);

	public StoreProductRequest ToProductRequest() => new()
	{
		Namespace = Namespace,
		OfferId = OfferId,
		ProductSlug = ProductSlug,
		Title = Title,
		ImageUrl = ImageUrl
	};
}

/// <summary>
/// Prices are in the currency's minor unit, as Epic reports them.
/// The formatted strings come from Epic, localized for the requested locale.
/// </summary>
public sealed class StorePrice
{
	public long OriginalPrice { get; init; }
	public long DiscountPrice { get; init; }
	public string? CurrencyCode { get; init; }
	public string? FormattedOriginalPrice { get; init; }
	public string? FormattedDiscountPrice { get; init; }

	public bool IsFree => DiscountPrice <= 0;

	public bool IsDiscounted => OriginalPrice > 0 && DiscountPrice < OriginalPrice;

	public int DiscountPercentage => IsDiscounted
		? (int)Math.Round((1 - (double)DiscountPrice / OriginalPrice) * 100, MidpointRounding.AwayFromZero)
		: 0;
}

/// <summary>
/// A time-limited discount. Epic's <c>discountPercentage</c> is the share of the
/// original price the buyer pays, so 0 means the offer is free during the window.
/// </summary>
public sealed class StorePromotion
{
	public DateTimeOffset StartDate { get; init; }
	public DateTimeOffset EndDate { get; init; }
	public int DiscountPercentage { get; init; }

	public bool IsFree => DiscountPercentage == 0;

	public bool IsActive(DateTimeOffset now) => StartDate <= now && now < EndDate;
}

public sealed class StoreSearchPage
{
	public static readonly StoreSearchPage Empty = new() { Results = [], Total = 0 };

	public required IReadOnlyList<StoreSearchResult> Results { get; init; }
	public int Total { get; init; }
}

public sealed class StoreOfferDetails
{
	public required StoreSearchResult Offer { get; init; }
	public string? LongDescription { get; init; }
	public string? Developer { get; init; }
	public string? Publisher { get; init; }
	public DateTimeOffset? ReleaseDate { get; init; }
	public bool IsCodeRedemptionOnly { get; init; }
	/// <summary>Epic's refund policy code, e.g. SELF_REFUNDABLE or NON_REFUNDABLE.</summary>
	public string? RefundType { get; init; }
	public IReadOnlyList<string> GalleryImageUrls { get; init; } = [];
	public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed class StoreOfferReference
{
	public required string Namespace { get; init; }
	public required string OfferId { get; init; }
}

/// <summary>
/// Navigation parameter for the native product page. Either the namespace and offer id
/// or a product slug is enough; the title and image let the page show something while loading.
/// </summary>
public sealed class StoreProductRequest
{
	public string? Namespace { get; init; }
	public string? OfferId { get; init; }
	public string? ProductSlug { get; init; }
	public string? Title { get; init; }
	public string? ImageUrl { get; init; }

	public bool HasOfferReference => !string.IsNullOrWhiteSpace(Namespace) && !string.IsNullOrWhiteSpace(OfferId);
}
