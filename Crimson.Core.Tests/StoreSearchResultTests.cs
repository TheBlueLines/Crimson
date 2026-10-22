using Crimson.Models;

namespace Crimson.Tests;

public sealed class StoreSearchResultTests
{
	[Fact]
	public void Parse_SelectsProductMappingAndPortraitImage()
	{
		const string json = """
			{
			  "data": {
				"Catalog": {
				  "searchStore": {
					"elements": [
					  {
						"title": "Example Game",
						"productSlug": "fallback-product/home",
						"urlSlug": "fallback-url",
						"catalogNs": {
						  "mappings": [
							{ "pageSlug": "mapped-product", "pageType": "productHome" }
						  ]
						},
						"keyImages": [
						  { "type": "OfferImageWide", "url": "https://cdn.example/wide" },
						  { "type": "OfferImageTall", "url": "https://cdn.example/tall" }
						]
					  }
					]
				  }
				}
			  }
			}
			""";

		var result = Assert.Single(StoreCatalogParser.Parse(json));

		Assert.Equal("Example Game", result.Title);
		Assert.Equal("mapped-product", result.ProductSlug);
		Assert.Equal("https://cdn.example/tall", result.ImageUrl);
	}

	[Fact]
	public void Parse_NormalizesProductSlugAndSkipsUnroutableResults()
	{
		const string json = """
			{
			  "data": {
				"Catalog": {
				  "searchStore": {
					"elements": [
					  { "title": "Routable", "productSlug": "routable/home", "keyImages": [] },
					  { "title": "Missing Slug", "keyImages": [] }
					]
				  }
				}
			  }
			}
			""";

		var result = Assert.Single(StoreCatalogParser.Parse(json));

		Assert.Equal("routable", result.ProductSlug);
		Assert.Null(result.ImageUrl);
	}

	[Fact]
	public void ParseSearchPage_ReadsOfferPriceItemsAndPaging()
	{
		const string json = """
			{
			  "data": {
				"Catalog": {
				  "searchStore": {
					"elements": [
					  {
						"title": "Hades",
						"id": "offer-1",
						"namespace": "min",
						"offerType": "BASE_GAME",
						"description": "Defy the god of the dead.",
						"effectiveDate": "2019-12-09T09:00:00.000Z",
						"seller": { "name": "Supergiant Games LLC" },
						"productSlug": null,
						"catalogNs": { "mappings": [ { "pageSlug": "hades" } ] },
						"items": [ { "id": "item-1", "namespace": "min" } ],
						"keyImages": [
						  { "type": "heroCarouselVideo", "url": "com.epicgames.video://abc" },
						  { "type": "OfferImageWide", "url": "https://cdn.example/wide.jpg" },
						  { "type": "OfferImageTall", "url": "https://cdn.example/tall.jpg" }
						],
						"price": {
						  "totalPrice": {
							"discountPrice": 1999,
							"originalPrice": 2499,
							"currencyCode": "USD",
							"fmtPrice": { "originalPrice": "$24.99", "discountPrice": "$19.99" }
						  }
						}
					  }
					],
					"paging": { "count": 40, "total": 1388 }
				  }
				}
			  }
			}
			""";

		var page = StoreCatalogParser.ParseSearchPage(json);

		Assert.Equal(1388, page.Total);
		var offer = Assert.Single(page.Results);
		Assert.Equal("offer-1", offer.OfferId);
		Assert.Equal("min", offer.Namespace);
		Assert.Equal("hades", offer.ProductSlug);
		Assert.Equal("Supergiant Games LLC", offer.Seller);
		Assert.Equal(["item-1"], offer.CatalogItemIds);
		Assert.Equal("https://cdn.example/tall.jpg", offer.ImageUrl);
		Assert.Equal("https://cdn.example/wide.jpg", offer.WideImageUrl);
		Assert.Equal(new DateTimeOffset(2019, 12, 9, 9, 0, 0, TimeSpan.Zero), offer.EffectiveDate);
		Assert.NotNull(offer.Price);
		Assert.Equal(1999, offer.Price.DiscountPrice);
		Assert.Equal("$24.99", offer.Price.FormattedOriginalPrice);
		Assert.True(offer.Price.IsDiscounted);
		Assert.Equal(20, offer.Price.DiscountPercentage);
	}

	[Fact]
	public void ParseSearchPage_KeepsOffersWithoutSlugWhenTheyCanOpenNatively()
	{
		const string json = """
			{ "data": { "Catalog": { "searchStore": { "elements": [
			  { "title": "New Game", "id": "offer-2", "namespace": "ns-2", "productSlug": null, "catalogNs": { "mappings": [] }, "offerMappings": [] }
			] } } } }
			""";

		var offer = Assert.Single(StoreCatalogParser.ParseSearchPage(json).Results);

		Assert.Null(offer.ProductSlug);
		Assert.True(offer.HasOfferReference);
		Assert.Empty(offer.CatalogItemIds);
	}

	[Fact]
	public void ParseSearchPage_ReadsFreeGamePromotions()
	{
		const string json = """
			{ "data": { "Catalog": { "searchStore": { "elements": [
			  {
				"title": "Giveaway",
				"id": "offer-3",
				"namespace": "ns-3",
				"price": { "totalPrice": { "discountPrice": 0, "originalPrice": 1299 } },
				"promotions": {
				  "promotionalOffers": [ { "promotionalOffers": [
					{ "startDate": "2026-10-15T15:00:00.000Z", "endDate": "2026-10-22T15:00:00.000Z", "discountSetting": { "discountType": "PERCENTAGE", "discountPercentage": 0 } }
				  ] } ],
				  "upcomingPromotionalOffers": []
				}
			  }
			] } } } }
			""";

		var offer = Assert.Single(StoreCatalogParser.ParseSearchPage(json).Results);

		Assert.NotNull(offer.CurrentPromotion);
		Assert.True(offer.CurrentPromotion.IsFree);
		Assert.True(offer.CurrentPromotion.IsActive(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero)));
		Assert.False(offer.CurrentPromotion.IsActive(new DateTimeOffset(2026, 10, 22, 15, 0, 0, TimeSpan.Zero)));
		Assert.Null(offer.UpcomingPromotion);
		Assert.True(offer.Price!.IsFree);
	}

	[Fact]
	public void ParseOfferDetails_ReadsDescriptionGalleryAndGenres()
	{
		const string json = """
			{ "data": { "Catalog": { "catalogOffer": {
			  "title": "Hades",
			  "id": "offer-1",
			  "namespace": "min",
			  "description": "Short",
			  "longDescription": "Long **text**",
			  "releaseDate": "2019-12-10T11:00:00.000Z",
			  "isCodeRedemptionOnly": false,
			  "developerDisplayName": null,
			  "publisherDisplayName": "Supergiant Games",
			  "customAttributes": [ { "key": "developerName", "value": "Supergiant Games" } ],
			  "offerMappings": [ { "pageSlug": "hades" } ],
			  "keyImages": [
				{ "type": "featuredMedia", "url": "https://cdn.example/1.jpg" },
				{ "type": "GalleryImage", "url": "https://cdn.example/2.jpg" },
				{ "type": "GalleryImage", "url": "https://cdn.example/1.jpg" },
				{ "type": "heroCarouselVideo", "url": "com.epicgames.video://x" }
			  ],
			  "tags": [
				{ "name": "Action", "groupName": "genre" },
				{ "name": "Single Player", "groupName": "feature" },
				{ "name": "Rogue-Lite", "groupName": "genre" }
			  ]
			} } } }
			""";

		var details = StoreCatalogParser.ParseOfferDetails(json);

		Assert.NotNull(details);
		Assert.Equal("Hades", details.Offer.Title);
		Assert.Equal("hades", details.Offer.ProductSlug);
		Assert.Equal("Long **text**", details.LongDescription);
		Assert.Equal("Supergiant Games", details.Developer);
		Assert.Equal("Supergiant Games", details.Publisher);
		Assert.False(details.IsCodeRedemptionOnly);
		Assert.Equal(["https://cdn.example/1.jpg", "https://cdn.example/2.jpg"], details.GalleryImageUrls);
		Assert.Equal(["Action", "Rogue-Lite"], details.Tags);
		Assert.Equal(new DateTimeOffset(2019, 12, 10, 11, 0, 0, TimeSpan.Zero), details.ReleaseDate);
	}

	[Fact]
	public void ParseOfferDetails_ReturnsNullForMissingOffer()
	{
		Assert.Null(StoreCatalogParser.ParseOfferDetails("""{ "data": { "Catalog": { "catalogOffer": null } } }"""));
	}

	[Fact]
	public void ParsePageMapping_ReadsSandboxAndOffer()
	{
		const string json = """
			{ "data": { "StorePageMapping": { "mapping": {
			  "pageSlug": "hades", "sandboxId": "min", "mappings": { "offerId": "offer-1" }
			} } } }
			""";

		var reference = StoreCatalogParser.ParsePageMapping(json);

		Assert.NotNull(reference);
		Assert.Equal("min", reference.Namespace);
		Assert.Equal("offer-1", reference.OfferId);
	}

	[Fact]
	public void ParsePageMapping_ReturnsNullWhenPageHasNoOffer()
	{
		const string json = """
			{ "data": { "StorePageMapping": { "mapping": { "sandboxId": "hub", "mappings": null } } } }
			""";

		Assert.Null(StoreCatalogParser.ParsePageMapping(json));
	}

	[Fact]
	public void Parsers_ThrowWhenGraphQlReportsAnErrorInsteadOfData()
	{
		const string json = """{ "errors": [ { "message": "Unknown argument" } ], "data": null }""";

		var error = Assert.Throws<StoreCatalogException>(() => StoreCatalogParser.ParseSearchPage(json));
		Assert.Equal("Unknown argument", error.Message);
		Assert.Throws<StoreCatalogException>(() => StoreCatalogParser.ParseOfferDetails(json));
		Assert.Throws<StoreCatalogException>(() => StoreCatalogParser.ParsePageMapping(json));
	}

	[Fact]
	public void Parsers_RejectUnreadableResponses()
	{
		Assert.Throws<StoreCatalogException>(() => StoreCatalogParser.ParseSearchPage("<html>blocked</html>"));
	}
}
