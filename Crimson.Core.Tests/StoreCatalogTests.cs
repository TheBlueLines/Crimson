using System.Net;
using System.Text.Json;
using Crimson.Core;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Crimson.ViewModels;
using Serilog;

namespace Crimson.Tests;

public sealed class StoreCatalogTests
{
	private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public void PriceText_ShowsDiscountWithStruckOriginal()
	{
		var price = StorePriceText.From(Offer(price: Price(1999, 2499, "$19.99", "$24.99")), Now);

		Assert.Equal("$19.99", price.Current);
		Assert.Equal("$24.99", price.Original);
		Assert.Equal("-20%", price.Discount);
	}

	[Fact]
	public void PriceText_ShowsGiveawaysAsFreeWithTheUsualPrice()
	{
		var price = StorePriceText.From(Offer(price: Price(0, 1299, "$0.00", "$12.99")), Now);

		Assert.Equal("Free", price.Current);
		Assert.Equal("$12.99", price.Original);
		Assert.Null(price.Discount);
	}

	[Fact]
	public void PriceText_ShowsComingSoonBeforeRelease()
	{
		var offer = Offer(price: Price(0, 0, "$0.00", "$0.00"), effectiveDate: Now.AddDays(30));

		Assert.Equal("Coming soon", StorePriceText.From(offer, Now).Current);
	}

	[Fact]
	public void StoreItem_CaptionsCurrentAndUpcomingGiveaways()
	{
		var current = StoreItem.Create(Offer(current: new StorePromotion
		{
			StartDate = Now.AddDays(-1),
			EndDate = Now.AddDays(2),
			DiscountPercentage = 0
		}), Now);
		var discount = StoreItem.Create(Offer(current: new StorePromotion
		{
			StartDate = Now.AddDays(-1),
			EndDate = Now.AddDays(2),
			DiscountPercentage = 40
		}), Now);

		Assert.StartsWith("Free until ", current.Caption);
		Assert.Null(discount.Caption);
	}

	[Fact]
	public void DescriptionFormatter_StripsMarkupToReadableParagraphs()
	{
		const string markdown = """
			<!--textBlock-->
			<!--text-->
			## Features

			- **Fast** combat with [six weapons](https://example.test)
			- Keeps some_file_name intact
			![screenshot](https://cdn.example/x.png)

			Tom &amp; Jerry<br>approved
			""";

		var text = StoreDescriptionFormatter.ToPlainText(markdown);

		Assert.Equal(
			"Features\n\n• Fast combat with six weapons\n• Keeps some_file_name intact\n\nTom & Jerry\napproved",
			text);
	}

	[Fact]
	public void WebLinks_BuildProductRedeemAndCheckoutPages()
	{
		Assert.Equal("https://store.epicgames.com/p/hades", StoreWebLinks.ProductPage("/hades/")!.AbsoluteUri);
		Assert.Null(StoreWebLinks.ProductPage(" "));
		Assert.Equal("https://store.epicgames.com/redeem", StoreWebLinks.Redeem.AbsoluteUri);
		Assert.Equal(
			"https://store.epicgames.com/purchase?offers=1-min-offer-1",
			StoreWebLinks.Purchase("min", "offer-1")!.AbsoluteUri);
		Assert.Null(StoreWebLinks.Purchase("min", null));
		Assert.True(EpicEndpointPolicy.IsAllowedStoreUri(StoreWebLinks.ProductPage("a b")!));
	}

	[Fact]
	public void Query_SearchesByRelevanceWithoutCategoryFilter()
	{
		var variables = new StoreQuery { Keywords = "  hades ", Start = 40 }.ToVariables(StoreRegion.Default, Now);

		Assert.Equal("hades", variables["keywords"]);
		Assert.Equal("relevancy", variables["sortBy"]);
		Assert.Equal("DESC", variables["sortDir"]);
		Assert.Equal(40, variables["start"]);
		Assert.False(variables.ContainsKey("category"));
		Assert.False(variables.ContainsKey("effectiveDate"));
	}

	[Fact]
	public void Query_BrowsingIsLimitedToGamesAndAlreadyReleasedOffers()
	{
		var variables = new StoreQuery { OnSaleOnly = true, Count = 500 }.ToVariables(new StoreRegion("HU", "hu-HU"), Now);

		Assert.Equal(StoreQuery.BrowseCategory, variables["category"]);
		Assert.Equal("releaseDate", variables["sortBy"]);
		Assert.Equal("[,2026-10-20T12:00:00.000Z]", variables["effectiveDate"]);
		Assert.Equal(true, variables["onSale"]);
		Assert.Equal(100, variables["count"]);
		Assert.Equal("HU", variables["country"]);
		Assert.Equal("hu-HU", variables["locale"]);
	}

	[Theory]
	[InlineData(StoreSortOrder.PriceLowToHigh, "currentPrice", "ASC")]
	[InlineData(StoreSortOrder.PriceHighToLow, "currentPrice", "DESC")]
	[InlineData(StoreSortOrder.Title, "title", "ASC")]
	public void Query_MapsSortOrders(StoreSortOrder order, string sortBy, string sortDir)
	{
		var variables = new StoreQuery { Keywords = "x", SortOrder = order }.ToVariables(StoreRegion.Default, Now);

		Assert.Equal(sortBy, variables["sortBy"]);
		Assert.Equal(sortDir, variables["sortDir"]);
	}

	[Fact]
	public void Region_FallsBackToUsForUnknownRegions()
	{
		Assert.Equal(new StoreRegion("HU", "hu-HU"), StoreRegion.From("hu", "hu-HU"));
		Assert.Equal(new StoreRegion("DE", "en-US"), StoreRegion.From("DE", string.Empty));
		Assert.Equal(StoreRegion.Default, StoreRegion.From("001", null));
	}

	[Fact]
	public void Ownership_MatchesLibraryByCatalogItem()
	{
		var game = Game("Hades", "item-1", installState: InstallState.Installed);
		var offer = Offer(items: ["item-1"]);

		Assert.Same(game, StoreOwnership.FindOwnedGame([Game("Other", "item-9"), game], offer));
		Assert.Equal(StoreOwnershipState.Installed, StoreOwnership.GetState(game));
		Assert.Equal(StoreOwnershipState.Owned, StoreOwnership.GetState(Game("Owned", "item-2")));
		Assert.Equal(StoreOwnershipState.NotOwned, StoreOwnership.GetState(null));
	}

	[Fact]
	public void Ownership_EditionsNeedEveryGrantedItem()
	{
		var baseGame = Game("Base", "item-base");
		var edition = Offer(type: "EDITION", items: ["item-base", "item-extra"]);

		Assert.Null(StoreOwnership.FindOwnedGame([baseGame], edition));
		Assert.Same(baseGame, StoreOwnership.FindOwnedGame([baseGame, Game("Extra", "item-extra", isDlc: true)], edition));
	}

	[Fact]
	public void Ownership_FallsBackToSlugOnlyForBaseGames()
	{
		var game = Game("Hades", "item-1", slug: "hades/home");
		var baseOffer = Offer(type: "BASE_GAME", slug: "hades", items: []);
		var addOn = Offer(type: "DLC", slug: "hades", items: []);

		Assert.Same(game, StoreOwnership.FindOwnedGame([game], baseOffer));
		Assert.Null(StoreOwnership.FindOwnedGame([game], addOn));
	}

	[Theory]
	[InlineData(null, StoreProductAction.Install)]
	[InlineData(InstallState.Installed, StoreProductAction.Play)]
	[InlineData(InstallState.NeedUpdate, StoreProductAction.Update)]
	[InlineData(InstallState.Broken, StoreProductAction.Repair)]
	public void ProductAction_OwnedGamesInstallOrPlayInApp(InstallState? state, StoreProductAction expected)
	{
		var game = Game("Hades", "item-1", installState: state);

		Assert.Equal(expected, StoreProductViewModel.GetAction(Details(Offer()), game, Now));
	}

	[Fact]
	public void ProductAction_UnownedOffersGoToTheWebStore()
	{
		Assert.Equal(StoreProductAction.Buy, StoreProductViewModel.GetAction(Details(Offer(price: Price(2499, 2499))), null, Now));
		Assert.Equal(StoreProductAction.Get, StoreProductViewModel.GetAction(Details(Offer(price: Price(0, 1299))), null, Now));
		Assert.Equal(
			StoreProductAction.Redeem,
			StoreProductViewModel.GetAction(Details(Offer(price: Price(0, 0)), redemptionOnly: true), null, Now));
		Assert.Equal(
			StoreProductAction.ViewOnWeb,
			StoreProductViewModel.GetAction(Details(Offer(price: Price(0, 0), effectiveDate: Now.AddDays(1))), null, Now));
		Assert.Equal(
			StoreProductAction.None,
			StoreProductViewModel.GetAction(Details(Offer()), Game("Soundtrack", "item-2", isDlc: true), Now));
	}

	[Fact]
	public async Task Catalog_PostsSearchToLauncherGraphQl()
	{
		var handler = new RecordingHandler(_ => Json("""{ "data": { "Catalog": { "searchStore": { "elements": [], "paging": { "total": 0 } } } } }"""));
		var catalog = CreateCatalog(handler);

		var page = await catalog.SearchAsync(new StoreQuery { Keywords = "hades" });

		Assert.Empty(page.Results);
		var request = Assert.Single(handler.Requests);
		Assert.Equal(HttpMethod.Post, request.Method);
		Assert.Equal("https://launcher.store.epicgames.com/graphql", request.Uri);
		using var body = JsonDocument.Parse(request.Body!);
		Assert.Contains("searchStore", body.RootElement.GetProperty("query").GetString());
		Assert.Equal("hades", body.RootElement.GetProperty("variables").GetProperty("keywords").GetString());
		Assert.Equal("HU", body.RootElement.GetProperty("variables").GetProperty("country").GetString());
	}

	[Fact]
	public async Task Catalog_KeepsOnlyCurrentAndUpcomingGiveaways()
	{
		const string feed = """
			{ "data": { "Catalog": { "searchStore": { "elements": [
			  { "title": "Sale", "id": "a", "namespace": "n", "promotions": { "promotionalOffers": [ { "promotionalOffers": [
				{ "startDate": "2026-10-19T00:00:00Z", "endDate": "2026-10-25T00:00:00Z", "discountSetting": { "discountPercentage": 40 } } ] } ] } },
			  { "title": "Next", "id": "b", "namespace": "n", "promotions": { "upcomingPromotionalOffers": [ { "promotionalOffers": [
				{ "startDate": "2026-10-22T15:00:00Z", "endDate": "2026-10-29T15:00:00Z", "discountSetting": { "discountPercentage": 0 } } ] } ] } },
			  { "title": "Now", "id": "c", "namespace": "n", "promotions": { "promotionalOffers": [ { "promotionalOffers": [
				{ "startDate": "2026-10-15T15:00:00Z", "endDate": "2026-10-22T15:00:00Z", "discountSetting": { "discountPercentage": 0 } } ] } ] } },
			  { "title": "Plain", "id": "d", "namespace": "n", "promotions": null }
			] } } } }
			""";
		var handler = new RecordingHandler(_ => Json(feed));
		var catalog = CreateCatalog(handler);

		var games = await catalog.GetFreeGamesAsync();

		Assert.Equal(["Now", "Next"], games.Select(game => game.Title));
		var request = Assert.Single(handler.Requests);
		Assert.Equal(HttpMethod.Get, request.Method);
		Assert.StartsWith("https://store-site-backend-static-ipv4.ak.epicgames.com/freeGamesPromotions?", request.Uri);
		Assert.Contains("country=HU", request.Uri);
	}

	[Fact]
	public async Task Catalog_ReportsUnreachableStore()
	{
		var catalog = CreateCatalog(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));

		await Assert.ThrowsAsync<StoreCatalogException>(() => catalog.SearchAsync(new StoreQuery()));
		await Assert.ThrowsAsync<StoreCatalogException>(() => catalog.GetOfferAsync("min", "offer-1"));
	}

	[Fact]
	public void StoreApiPolicy_AllowsOnlyCatalogHosts()
	{
		Assert.Equal(
			"store-site-backend-static-ipv4.ak.epicgames.com",
			EpicEndpointPolicy.RequireStoreApiUri("https://store-site-backend-static-ipv4.ak.epicgames.com/freeGamesPromotions").Host);
		Assert.Throws<InvalidOperationException>(() => EpicEndpointPolicy.RequireStoreApiUri("https://store.epicgames.com/graphql"));
		Assert.Throws<InvalidOperationException>(() => EpicEndpointPolicy.RequireStoreApiUri("http://launcher.store.epicgames.com/graphql"));
	}

	internal static StoreSearchResult Offer(
		StorePrice? price = null,
		DateTimeOffset? effectiveDate = null,
		StorePromotion? current = null,
		string type = "BASE_GAME",
		string? slug = "hades",
		IReadOnlyList<string>? items = null,
		string title = "Hades",
		string offerId = "offer-1") => new()
	{
		Title = title,
		Namespace = "min",
		OfferId = offerId,
		OfferType = type,
		ProductSlug = slug,
		EffectiveDate = effectiveDate ?? Now.AddYears(-1),
		Price = price ?? Price(2499, 2499),
		CurrentPromotion = current,
		CatalogItemIds = items ?? ["item-1"]
	};

	internal static StorePrice Price(long discount, long original, string? formattedDiscount = null, string? formattedOriginal = null) => new()
	{
		DiscountPrice = discount,
		OriginalPrice = original,
		CurrencyCode = "USD",
		FormattedDiscountPrice = formattedDiscount,
		FormattedOriginalPrice = formattedOriginal
	};

	internal static StoreOfferDetails Details(StoreSearchResult offer, bool redemptionOnly = false) => new()
	{
		Offer = offer,
		IsCodeRedemptionOnly = redemptionOnly
	};

	internal static Game Game(
		string appName,
		string catalogItemId,
		InstallState? installState = null,
		bool isDlc = false,
		string? slug = null) => new()
	{
		AppName = appName,
		AppTitle = appName,
		AssetInfos = new AssetInfos
		{
			Windows = new Asset
			{
				AppName = appName,
				BuildVersion = "1.0",
				CatalogItemId = catalogItemId,
				Namespace = "min"
			}
		},
		Metadata = new Metadata
		{
			Id = catalogItemId,
			KeyImages = [],
			MainGameItem = isDlc ? new MainGameItem() : null!,
			CustomAttributes = slug is null
				? null!
				: new CustomAttributes { ComEpicgamesAppProductSlug = new ComEpicgamesAppProductSlug { Value = slug } }
		},
		LocalAppState = installState is null ? null : new LocalAppState { InstallStatus = installState.Value }
	};

	private static EpicStoreCatalog CreateCatalog(RecordingHandler handler) => new(
		new LoggerConfiguration().CreateLogger(),
		new HttpClient(handler),
		() => new StoreRegion("HU", "en-US"),
		new FixedTimeProvider(Now));

	private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
	{
		Content = new StringContent(json)
	};

	internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
	{
		public override DateTimeOffset GetUtcNow() => now;
	}

	private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
	{
		public List<(HttpMethod Method, string Uri, string? Body)> Requests { get; } = [];

		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
			Requests.Add((request.Method, request.RequestUri!.AbsoluteUri, body));
			return respond(request);
		}
	}
}
