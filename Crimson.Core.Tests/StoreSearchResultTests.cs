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

		var result = Assert.Single(StoreSearchResultParser.Parse(json));

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

		var result = Assert.Single(StoreSearchResultParser.Parse(json));

		Assert.Equal("routable", result.ProductSlug);
		Assert.Null(result.ImageUrl);
	}
}
