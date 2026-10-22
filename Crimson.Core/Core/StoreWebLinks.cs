using System;

namespace Crimson.Core;

/// <summary>
/// Web store pages for the flows the public catalog API cannot cover: checkout,
/// code redemption, and product content such as reviews or system requirements.
/// </summary>
public static class StoreWebLinks
{
	public static readonly Uri Home = new("https://store.epicgames.com/");

	public static readonly Uri Redeem = new(Home, "redeem");

	public static Uri? ProductPage(string? productSlug)
	{
		var slug = productSlug?.Trim().Trim('/');
		if (string.IsNullOrEmpty(slug))
			return null;

		return new Uri(Home, $"p/{Uri.EscapeDataString(slug)}");
	}

	/// <summary>
	/// Epic's checkout for a single offer, in the launcher's "1-namespace-offerId" form.
	/// </summary>
	public static Uri? Purchase(string? nameSpace, string? offerId)
	{
		if (string.IsNullOrWhiteSpace(nameSpace) || string.IsNullOrWhiteSpace(offerId))
			return null;

		var offer = Uri.EscapeDataString($"1-{nameSpace.Trim()}-{offerId.Trim()}");
		return new Uri(Home, $"purchase?offers={offer}");
	}
}
