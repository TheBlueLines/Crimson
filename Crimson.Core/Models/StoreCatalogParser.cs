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
			RefundType = GetString(offer, "refundType"),
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

	/// <summary>
	/// Reads the combined product page query. Epic answers each part on its own, so a part that
	/// errored comes back null next to the others; only a response without any data is an error.
	/// </summary>
	public static StoreProductPageInfo ParseProductPage(string json)
	{
		using var document = ParseDocument(json);
		var root = document.RootElement;
		if (!TryGetProperty(root, "data", out var data) || data.ValueKind != JsonValueKind.Object)
		{
			ThrowIfGraphQlErrors(root);
			return StoreProductPageInfo.Empty;
		}

		var media = new List<StoreMediaItem>();
		var requirements = new List<StoreRequirement>();
		var socialLinks = new List<StoreSocialLink>();
		IReadOnlyList<string> audio = [];
		IReadOnlyList<string> text = [];
		string? longDescription = null;
		if (TryGetPath(data, out var configurations, "Product", "sandbox", "configuration") &&
			configurations.ValueKind == JsonValueKind.Array)
		{
			foreach (var configuration in configurations.EnumerateArray())
			{
				if (!TryGetProperty(configuration, "configs", out var configs) || configs.ValueKind != JsonValueKind.Object)
					continue;

				longDescription ??= NullIfBlank(GetString(configs, "longDescription"));
				ReadMedia(configs, media);
				ReadRequirements(configs, requirements);
				ReadSocialLinks(configs, socialLinks);
				if (audio.Count == 0)
					audio = GetStrings(configs, "supportedAudio");
				if (text.Count == 0)
					text = GetStrings(configs, "supportedText");
			}
		}

		var addOns = new List<StoreSearchResult>();
		if (TryGetPath(data, out var elements, "Catalog", "searchStore", "elements") &&
			elements.ValueKind == JsonValueKind.Array)
		{
			foreach (var element in elements.EnumerateArray())
			{
				var offer = ParseOffer(element);
				if (offer is not null)
					addOns.Add(offer);
			}
		}

		return new StoreProductPageInfo
		{
			Media = media,
			LongDescription = longDescription,
			Requirements = requirements,
			AudioLanguages = audio,
			TextLanguages = text,
			SocialLinks = socialLinks,
			PlayerRating = ReadPlayerRating(data),
			CriticRating = ReadCriticRating(data),
			AddOns = addOns
		};
	}

	private static void ReadMedia(JsonElement configs, List<StoreMediaItem> media)
	{
		if (!TryGetProperty(configs, "keyImages", out var images) || images.ValueKind != JsonValueKind.Array)
			return;

		foreach (var image in images.EnumerateArray())
		{
			var type = GetString(image, "type");
			var url = GetString(image, "url");
			if (string.IsNullOrWhiteSpace(url))
				continue;

			if (Array.IndexOf(GalleryImageTypes, type) >= 0 && IsHttpsUrl(url))
			{
				if (!media.Exists(item => item.ImageUrl == url))
					media.Add(new StoreMediaItem(url, IsVideo: false));
				continue;
			}

			// Videos use com.epicgames.video://<id>?cover=<https picture>; only the cover is playable here.
			if (url.StartsWith("com.epicgames.video://", StringComparison.OrdinalIgnoreCase) &&
				Uri.TryCreate(url, UriKind.Absolute, out var videoUri))
			{
				var cover = GetQueryValue(videoUri, "cover");
				if (IsHttpsUrl(cover) && !media.Exists(item => item.ImageUrl == cover))
					media.Add(new StoreMediaItem(cover!, IsVideo: true));
			}
		}
	}

	private static void ReadRequirements(JsonElement configs, List<StoreRequirement> requirements)
	{
		if (!TryGetPath(configs, out var rows, "technicalRequirements", "windows") || rows.ValueKind != JsonValueKind.Array)
			return;

		foreach (var row in rows.EnumerateArray())
		{
			var title = GetString(row, "title")?.Trim();
			var minimum = GetString(row, "minimum")?.Trim() ?? string.Empty;
			var recommended = GetString(row, "recommended")?.Trim() ?? string.Empty;
			if (string.IsNullOrWhiteSpace(title) || (minimum.Length == 0 && recommended.Length == 0))
				continue;

			requirements.Add(new StoreRequirement(title, minimum, recommended));
		}
	}

	private static void ReadSocialLinks(JsonElement configs, List<StoreSocialLink> links)
	{
		if (!TryGetProperty(configs, "socialLinks", out var items) || items.ValueKind != JsonValueKind.Array)
			return;

		foreach (var item in items.EnumerateArray())
		{
			var platform = GetString(item, "platform");
			var url = GetString(item, "url");
			if (string.IsNullOrWhiteSpace(platform) || !IsHttpsUrl(url))
				continue;

			// Epic names them linkTwitter, linkDiscord, ...; show the network's name.
			var name = platform.StartsWith("link", StringComparison.OrdinalIgnoreCase) && platform.Length > 4
				? platform[4..]
				: platform;
			links.Add(new StoreSocialLink(char.ToUpperInvariant(name[0]) + name[1..], new Uri(url!)));
		}
	}

	private static StorePlayerRating? ReadPlayerRating(JsonElement data)
	{
		if (!TryGetPath(data, out var result, "RatingsPolls", "getProductResult") ||
			!TryGetProperty(result, "averageRating", out var average) ||
			average.ValueKind != JsonValueKind.Number ||
			!average.TryGetDouble(out var value) || value <= 0)
			return null;

		var highlights = new List<StoreRatingHighlight>();
		if (TryGetProperty(result, "pollResult", out var polls) && polls.ValueKind == JsonValueKind.Array)
		{
			foreach (var poll in polls.EnumerateArray())
			{
				if (!TryGetProperty(poll, "localizations", out var localization))
					continue;
				var title = GetString(localization, "resultTitle");
				if (!string.IsNullOrWhiteSpace(title))
					highlights.Add(new StoreRatingHighlight(GetString(localization, "resultText") ?? string.Empty, title));
			}
		}

		return new StorePlayerRating(Math.Round(value, 1), highlights);
	}

	private static StoreCriticRating? ReadCriticRating(JsonElement data)
	{
		if (!TryGetPath(data, out var reviews, "OpenCritic", "productReviews") || reviews.ValueKind != JsonValueKind.Object)
			return null;

		var score = GetPositiveInt(reviews, "openCriticScore");
		var recommended = GetPositiveInt(reviews, "percentRecommended");
		var award = NullIfBlank(GetString(reviews, "award"));
		return score is null && recommended is null ? null : new StoreCriticRating(score, recommended, award);
	}

	private static int? GetPositiveInt(JsonElement element, string propertyName) =>
		TryGetProperty(element, propertyName, out var property) &&
		property.ValueKind == JsonValueKind.Number &&
		property.TryGetDouble(out var value) && value > 0
			? (int)Math.Round(value)
			: null;

	private static IReadOnlyList<string> GetStrings(JsonElement element, string propertyName)
	{
		if (!TryGetProperty(element, propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
			return [];

		var values = new List<string>();
		foreach (var item in array.EnumerateArray())
		{
			if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
				values.Add(item.GetString()!.Trim());
		}

		return values;
	}

	private static string? GetQueryValue(Uri uri, string name)
	{
		foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
		{
			var separator = pair.IndexOf('=');
			if (separator > 0 && string.Equals(pair[..separator], name, StringComparison.OrdinalIgnoreCase))
				return Uri.UnescapeDataString(pair[(separator + 1)..]);
		}

		return null;
	}

	private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

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
