using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Crimson.Core;
using Crimson.Models;

namespace Crimson.ViewModels;

/// <summary>
/// A store offer as shown on a card: its price line and whether it is already in the library.
/// </summary>
public sealed partial class StoreItem : ObservableObject
{
	public required StoreSearchResult Offer { get; init; }
	public string Title => Offer.Title;
	public string? ImageUrl => Offer.ImageUrl;
	public required string PriceText { get; init; }
	public string? OriginalPriceText { get; init; }
	public string? DiscountText { get; init; }
	public string? Caption { get; init; }

	public bool HasOriginalPrice => !string.IsNullOrEmpty(OriginalPriceText);
	public bool HasDiscount => !string.IsNullOrEmpty(DiscountText);
	public bool HasCaption => !string.IsNullOrEmpty(Caption);

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsOwned))]
	[NotifyPropertyChangedFor(nameof(OwnershipText))]
	[NotifyPropertyChangedFor(nameof(ShowPrice))]
	private StoreOwnershipState _ownershipState;

	public string? OwnedAppName { get; private set; }

	public bool IsOwned => OwnershipState != StoreOwnershipState.NotOwned;

	public bool ShowPrice => !IsOwned;

	public string OwnershipText => OwnershipState switch
	{
		StoreOwnershipState.Installed => "Installed",
		StoreOwnershipState.Owned => "In library",
		_ => string.Empty
	};

	public static StoreItem Create(StoreSearchResult offer, DateTimeOffset now)
	{
		var price = StorePriceText.From(offer, now);
		return new StoreItem
		{
			Offer = offer,
			PriceText = price.Current,
			OriginalPriceText = price.Original,
			DiscountText = price.Discount,
			Caption = GetPromotionCaption(offer, now)
		};
	}

	public void ApplyOwnership(IReadOnlyCollection<Game> library)
	{
		var game = StoreOwnership.FindOwnedGame(library, Offer);
		OwnedAppName = game?.AppName;
		OwnershipState = StoreOwnership.GetState(game);
	}

	private static string? GetPromotionCaption(StoreSearchResult offer, DateTimeOffset now)
	{
		if (offer.CurrentPromotion is { IsFree: true } current && current.IsActive(now))
			return $"Free until {FormatDate(current.EndDate)}";

		if (offer.UpcomingPromotion is { IsFree: true } upcoming && upcoming.StartDate > now)
			return $"Free from {FormatDate(upcoming.StartDate)}";

		return null;
	}

	private static string FormatDate(DateTimeOffset value) =>
		value.ToLocalTime().ToString("MMM d", CultureInfo.CurrentCulture);
}

/// <summary>
/// The price line for an offer: the amount to pay, the struck-through original, and the discount.
/// </summary>
public readonly record struct StorePriceText(string Current, string? Original, string? Discount)
{
	public static StorePriceText From(StoreSearchResult offer, DateTimeOffset now)
	{
		if (offer.EffectiveDate > now)
			return new StorePriceText("Coming soon", null, null);

		var price = offer.Price;
		if (price is null)
			return new StorePriceText(string.Empty, null, null);

		if (price.IsFree)
		{
			// A giveaway shows the price it normally sells for.
			return price.OriginalPrice > 0
				? new StorePriceText("Free", Format(price.FormattedOriginalPrice, price.OriginalPrice, price.CurrencyCode), null)
				: new StorePriceText("Free", null, null);
		}

		var current = Format(price.FormattedDiscountPrice, price.DiscountPrice, price.CurrencyCode);
		return price.IsDiscounted
			? new StorePriceText(
				current,
				Format(price.FormattedOriginalPrice, price.OriginalPrice, price.CurrencyCode),
				$"-{price.DiscountPercentage}%")
			: new StorePriceText(current, null, null);
	}

	private static string Format(string? formatted, long minorUnits, string? currencyCode)
	{
		if (!string.IsNullOrWhiteSpace(formatted))
			return formatted;

		var amount = (minorUnits / 100m).ToString("0.00", CultureInfo.CurrentCulture);
		return string.IsNullOrWhiteSpace(currencyCode) ? amount : $"{amount} {currencyCode}";
	}
}
