using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Crimson.Models;

public sealed class StoreCatalogException(string message, Exception? innerException = null)
	: Exception(message, innerException);

/// <summary>
/// Reads the Epic Games Store GraphQL responses. Epic leaves most fields nullable and
/// changes shapes between page types, so every lookup tolerates a missing or mistyped value.
/// </summary>
internal static class StoreCatalogParser
{
	private static readonly string[] TallImageTypes = ["OfferImageTall", "DieselStoreFrontTall", "Thumbnail", "CodeRedemption_340x440"];
	private static readonly string[] WideImageTypes = ["OfferImageWide", "DieselStoreFrontWide", "featuredMedia"];
	private static readonly string[] GalleryImageTypes = ["featuredMedia", "GalleryImage"];

	public static IReadOnlyList<StoreSearchResult> Parse(string json) => ParseSearchPage(json).Results;

	public static StoreSearchPage ParseSearchPage(string json)
	{
		using var document = ParseDocument(json);
		var root = document.RootElement;
		if (!TryGetPath(root, out var searchStore, "data", "Catalog", "searchStore"))
		{
			ThrowIfGraphQlErrors(root);
			return StoreSearchPage.Empty;
		}

		var results = new List<StoreSearchResult>();
		if (TryGetProperty(searchStore, "elements", out var elements) && elements.ValueKind == JsonValueKind.Array)
		{
			foreach (var element in elements.EnumerateArray())
			{
				var result = ParseOffer(element);
				if (result is not null)
					results.Add(result);
			}
		}

		var total = TryGetPath(searchStore, out var totalElement, "paging", "total") &&
					totalElement.ValueKind == JsonValueKind.Number &&
					totalElement.TryGetInt32(out var value)
			? value
			: results.Count;
		return new StoreSearchPage { Results = results, Total = total };
	}

	public static StoreOfferDetails? ParseOfferDetails(string json)
	{
		using var document = ParseDocument(json);
		var root = document.RootElement;
		if (!TryGetPath(root, out var offer, "data", "Catalog", "catalogOffer") ||
			offer.ValueKind != JsonValueKind.Object)
		{
			ThrowIfGraphQlErrors(root);
			return null;
		}

		var summary = ParseOffer(offer);
		if (summary is null)
			return null;

		return new StoreOfferDetails
		{
			Offer = summary,
			LongDescription = GetString(offer, "longDescription"),
			Developer = GetString(offer, "developerDisplayName") ?? GetCustomAttribute(offer, "developerName"),
			Publisher = GetString(offer, "publisherDisplayName") ?? GetCustomAttribute(offer, "publisherName"),
			ReleaseDate = GetDate(offer, "releaseDate") ?? summary.EffectiveDate,
			IsCodeRedemptionOnly = TryGetProperty(offer, "isCodeRedemptionOnly", out var redemption) &&
								   redemption.ValueKind == JsonValueKind.True,
			GalleryImageUrls = GetImageUrls(offer, GalleryImageTypes),
			Tags = GetTags(offer)
		};
	}

	public static StoreOfferReference? ParsePageMapping(string json)
	{
		using var document = ParseDocument(json);
		var root = document.RootElement;
		if (!TryGetPath(root, out var mapping, "data", "StorePageMapping", "mapping") ||
			mapping.ValueKind != JsonValueKind.Object)
		{
			ThrowIfGraphQlErrors(root);
			return null;
		}

		var nameSpace = GetString(mapping, "sandboxId");
		if (!TryGetProperty(mapping, "mappings", out var offers))
			return null;

		// Epic returns a single object here, but tolerate an array in case that changes.
		var offerId = offers.ValueKind == JsonValueKind.Array
			? FirstString(offers, "offerId")
			: GetString(offers, "offerId");
		if (string.IsNullOrWhiteSpace(nameSpace) || string.IsNullOrWhiteSpace(offerId))
			return null;

		return new StoreOfferReference { Namespace = nameSpace, OfferId = offerId };
	}

	private static StoreSearchResult? ParseOffer(JsonElement element)
	{
		var title = GetString(element, "title");
		if (string.IsNullOrWhiteSpace(title))
			return null;

		var nameSpace = GetString(element, "namespace");
		var offerId = GetString(element, "id");
		var productSlug = GetProductSlug(element);
		if (string.IsNullOrWhiteSpace(productSlug) &&
			(string.IsNullOrWhiteSpace(nameSpace) || string.IsNullOrWhiteSpace(offerId)))
			return null;

		return new StoreSearchResult
		{
			Title = title,
			ProductSlug = productSlug,
			Namespace = nameSpace,
			OfferId = offerId,
			OfferType = GetString(element, "offerType"),
			Description = GetString(element, "description"),
			Seller = TryGetProperty(element, "seller", out var seller) ? GetString(seller, "name") : null,
			ImageUrl = GetImageUrl(element, TallImageTypes),
			WideImageUrl = GetImageUrl(element, WideImageTypes),
			EffectiveDate = GetDate(element, "effectiveDate"),
			Price = GetPrice(element),
			CurrentPromotion = GetPromotion(element, "promotionalOffers"),
			UpcomingPromotion = GetPromotion(element, "upcomingPromotionalOffers"),
			CatalogItemIds = GetCatalogItemIds(element)
		};
	}

	private static string? GetProductSlug(JsonElement element)
	{
		var slug = GetMappingSlug(element, "catalogNs")
				   ?? GetMappingSlug(element, "offerMappings")
				   ?? GetString(element, "productSlug")
				   ?? GetString(element, "urlSlug");
		if (string.IsNullOrWhiteSpace(slug))
			return null;

		slug = slug.Trim('/');
		var separator = slug.IndexOf('/');
		return separator >= 0 ? slug[..separator] : slug;
	}

	private static string? GetMappingSlug(JsonElement element, string propertyName)
	{
		if (!TryGetProperty(element, propertyName, out var container))
			return null;

		JsonElement mappings;
		if (container.ValueKind == JsonValueKind.Array)
		{
			mappings = container;
		}
		else if (!TryGetProperty(container, "mappings", out mappings) ||
				 mappings.ValueKind != JsonValueKind.Array)
		{
			return null;
		}

		return FirstString(mappings, "pageSlug");
	}

	private static string? GetImageUrl(JsonElement element, IEnumerable<string> preferredTypes)
	{
		if (!TryGetProperty(element, "keyImages", out var images) || images.ValueKind != JsonValueKind.Array)
			return null;

		foreach (var preferredType in preferredTypes)
		{
			foreach (var image in images.EnumerateArray())
			{
				if (!string.Equals(GetString(image, "type"), preferredType, StringComparison.OrdinalIgnoreCase))
					continue;

				var url = GetString(image, "url");
				if (IsHttpsUrl(url))
					return url;
			}
		}

		return null;
	}

	private static IReadOnlyList<string> GetImageUrls(JsonElement element, string[] types)
	{
		if (!TryGetProperty(element, "keyImages", out var images) || images.ValueKind != JsonValueKind.Array)
			return [];

		var urls = new List<string>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var image in images.EnumerateArray())
		{
			var type = GetString(image, "type");
			var url = GetString(image, "url");
			// Video entries use a com.epicgames.video:// scheme, which Image cannot render.
			if (type is null || Array.IndexOf(types, type) < 0 || !IsHttpsUrl(url) || !seen.Add(url!))
				continue;

			urls.Add(url!);
		}

		return urls;
	}

	private static StorePrice? GetPrice(JsonElement element)
	{
		if (!TryGetPath(element, out var totalPrice, "price", "totalPrice") ||
			totalPrice.ValueKind != JsonValueKind.Object)
			return null;

		var formatted = TryGetProperty(totalPrice, "fmtPrice", out var fmtPrice) ? fmtPrice : default;
		return new StorePrice
		{
			OriginalPrice = GetInt64(totalPrice, "originalPrice"),
			DiscountPrice = GetInt64(totalPrice, "discountPrice"),
			CurrencyCode = GetString(totalPrice, "currencyCode"),
			FormattedOriginalPrice = GetString(formatted, "originalPrice"),
			FormattedDiscountPrice = GetString(formatted, "discountPrice")
		};
	}

	private static StorePromotion? GetPromotion(JsonElement element, string propertyName)
	{
		if (!TryGetPath(element, out var groups, "promotions", propertyName) ||
			groups.ValueKind != JsonValueKind.Array)
			return null;

		foreach (var group in groups.EnumerateArray())
		{
			if (!TryGetProperty(group, "promotionalOffers", out var offers) || offers.ValueKind != JsonValueKind.Array)
				continue;

			foreach (var offer in offers.EnumerateArray())
			{
				var start = GetDate(offer, "startDate");
				var end = GetDate(offer, "endDate");
				if (start is null || end is null ||
					!TryGetPath(offer, out var percentage, "discountSetting", "discountPercentage") ||
					percentage.ValueKind != JsonValueKind.Number ||
					!percentage.TryGetInt32(out var discountPercentage))
					continue;

				return new StorePromotion
				{
					StartDate = start.Value,
					EndDate = end.Value,
					DiscountPercentage = discountPercentage
				};
			}
		}

		return null;
	}

	private static IReadOnlyList<string> GetCatalogItemIds(JsonElement element)
	{
		if (!TryGetProperty(element, "items", out var items) || items.ValueKind != JsonValueKind.Array)
			return [];

		var ids = new List<string>();
		foreach (var item in items.EnumerateArray())
		{
			var id = GetString(item, "id");
			if (!string.IsNullOrWhiteSpace(id))
				ids.Add(id);
		}

		return ids;
	}

	private static IReadOnlyList<string> GetTags(JsonElement element)
	{
		if (!TryGetProperty(element, "tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
			return [];

		var names = new List<string>();
		foreach (var tag in tags.EnumerateArray())
		{
			// Only genres read well as chips; features and platforms are noise here.
			if (!string.Equals(GetString(tag, "groupName"), "genre", StringComparison.OrdinalIgnoreCase))
				continue;

			var name = GetString(tag, "name");
			if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name))
				names.Add(name);
		}

		return names;
	}

	private static string? GetCustomAttribute(JsonElement element, string key)
	{
		if (!TryGetProperty(element, "customAttributes", out var attributes) ||
			attributes.ValueKind != JsonValueKind.Array)
			return null;

		foreach (var attribute in attributes.EnumerateArray())
		{
			if (string.Equals(GetString(attribute, "key"), key, StringComparison.Ordinal))
				return GetString(attribute, "value");
		}

		return null;
	}

	private static void ThrowIfGraphQlErrors(JsonElement root)
	{
		if (!TryGetProperty(root, "errors", out var errors) ||
			errors.ValueKind != JsonValueKind.Array ||
			errors.GetArrayLength() == 0)
			return;

		var message = FirstString(errors, "message") ?? "The store returned an error.";
		throw new StoreCatalogException(message);
	}

	private static JsonDocument ParseDocument(string json)
	{
		try
		{
			return JsonDocument.Parse(json);
		}
		catch (JsonException ex)
		{
			throw new StoreCatalogException("The store returned an unreadable response.", ex);
		}
	}

	private static string? FirstString(JsonElement array, string propertyName)
	{
		foreach (var item in array.EnumerateArray())
		{
			var value = GetString(item, propertyName);
			if (!string.IsNullOrWhiteSpace(value))
				return value;
		}

		return null;
	}

	private static DateTimeOffset? GetDate(JsonElement element, string propertyName) =>
		DateTimeOffset.TryParse(
			GetString(element, propertyName),
			CultureInfo.InvariantCulture,
			DateTimeStyles.AssumeUniversal,
			out var value)
			? value
			: null;

	private static long GetInt64(JsonElement element, string propertyName) =>
		TryGetProperty(element, propertyName, out var property) &&
		property.ValueKind == JsonValueKind.Number &&
		property.TryGetInt64(out var value)
			? value
			: 0;

	private static bool IsHttpsUrl(string? value) =>
		Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
		uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

	private static string? GetString(JsonElement element, string propertyName) =>
		TryGetProperty(element, propertyName, out var property) && property.ValueKind == JsonValueKind.String
			? property.GetString()
			: null;

	private static bool TryGetPath(JsonElement element, out JsonElement property, params string[] path)
	{
		property = element;
		foreach (var name in path)
		{
			if (!TryGetProperty(property, name, out property))
				return false;
		}

		return true;
	}

	private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement property)
	{
		if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out property))
			return true;

		property = default;
		return false;
	}
}
