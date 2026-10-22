using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Crimson.Models;

public sealed class StoreSearchResult
{
	public required string Title { get; init; }
	public required string ProductSlug { get; init; }
	public string? ImageUrl { get; init; }
}

internal static class StoreSearchResultParser
{
	public static IReadOnlyList<StoreSearchResult> Parse(string json)
	{
		using var document = JsonDocument.Parse(json);
		if (!TryGetProperty(document.RootElement, "data", out var data) ||
			!TryGetProperty(data, "Catalog", out var catalog) ||
			!TryGetProperty(catalog, "searchStore", out var searchStore) ||
			!TryGetProperty(searchStore, "elements", out var elements) ||
			elements.ValueKind != JsonValueKind.Array)
			return [];

		var results = new List<StoreSearchResult>();
		foreach (var element in elements.EnumerateArray())
		{
			var title = GetString(element, "title");
			var productSlug = GetProductSlug(element);
			if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(productSlug))
				continue;

			results.Add(new StoreSearchResult
			{
				Title = title,
				ProductSlug = productSlug,
				ImageUrl = GetImageUrl(element)
			});
		}

		return results;
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

		foreach (var mapping in mappings.EnumerateArray())
		{
			var pageSlug = GetString(mapping, "pageSlug");
			if (!string.IsNullOrWhiteSpace(pageSlug))
				return pageSlug;
		}

		return null;
	}

	private static string? GetImageUrl(JsonElement element)
	{
		if (!TryGetProperty(element, "keyImages", out var images) || images.ValueKind != JsonValueKind.Array)
			return null;

		foreach (var preferredType in new[] { "OfferImageTall", "Thumbnail", "OfferImageWide" })
		{
			foreach (var image in images.EnumerateArray())
			{
				if (string.Equals(GetString(image, "type"), preferredType, StringComparison.OrdinalIgnoreCase))
					return GetString(image, "url");
			}
		}

		return null;
	}

	private static string? GetString(JsonElement element, string propertyName) =>
		TryGetProperty(element, propertyName, out var property) && property.ValueKind == JsonValueKind.String
			? property.GetString()
			: null;

	private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement property)
	{
		if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out property))
			return true;

		property = default;
		return false;
	}
}
