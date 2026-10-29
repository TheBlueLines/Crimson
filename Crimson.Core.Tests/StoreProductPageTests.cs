using Crimson.Models;
using Crimson.Utils;
using Crimson.ViewModels;

namespace Crimson.Tests;

public sealed class StoreProductPageTests
{
	// Trimmed from Epic's real answer for Sker Ritual.
	private const string ProductPageJson = """
		{ "data": {
			"Product": { "sandbox": { "configuration": [
				{ "configs": {
					"technicalRequirements": { "windows": [
						{ "title": "CPU", "minimum": "Intel Core i5-4460", "recommended": "Intel Core i5-10500" },
						{ "title": "Notes", "minimum": "", "recommended": "" }
					] },
					"socialLinks": [
						{ "platform": "linkDiscord", "url": "https://discord.gg/example" },
						{ "platform": "linkTwitter", "url": "javascript:alert(1)" }
					],
					"supportedAudio": ["English"],
					"supportedText": ["English", "German"]
				} },
				{ "configs": {
					"keyImages": [
						{ "type": "heroCarouselVideo", "url": "com.epicgames.video://f68b?cover=https%3A%2F%2Fcdn1.epicgames.com%2Fvideo.png" },
						{ "type": "featuredMedia", "url": "https://cdn1.epicgames.com/shot-1.png" },
						{ "type": "featuredMedia", "url": "https://cdn1.epicgames.com/shot-1.png" }
					],
					"longDescription": "# Arm up\nTear through waves."
				} }
			] } },
			"RatingsPolls": { "getProductResult": {
				"averageRating": 4.07,
				"pollResult": [ { "localizations": { "resultTitle": "Great Boss Battles", "resultText": "This game has" } } ]
			} },
			"OpenCritic": { "productReviews": { "openCriticScore": 72, "percentRecommended": 55, "award": "Fair" } },
			"Catalog": { "searchStore": { "elements": [
				{ "title": "Shrunken Head DLC", "id": "addon-1", "namespace": "sker", "keyImages": [] }
			] } }
		} }
		""";

	[Fact]
	public void ParseProductPage_ReadsEveryPartOfThePage()
	{
		var page = StoreCatalogParser.ParseProductPage(ProductPageJson);

		Assert.Equal(
			[new StoreMediaItem("https://cdn1.epicgames.com/video.png", true), new StoreMediaItem("https://cdn1.epicgames.com/shot-1.png", false)],
			page.Media);
		Assert.Equal("# Arm up\nTear through waves.", page.LongDescription);
		Assert.Equal([new StoreRequirement("CPU", "Intel Core i5-4460", "Intel Core i5-10500")], page.Requirements);
		Assert.Equal(["English"], page.AudioLanguages);
		Assert.Equal(["English", "German"], page.TextLanguages);
		var link = Assert.Single(page.SocialLinks);
		Assert.Equal("Discord", link.Platform);
		Assert.Equal(4.1, page.PlayerRating!.Average);
		Assert.Equal(new StoreRatingHighlight("This game has", "Great Boss Battles"), Assert.Single(page.PlayerRating.Highlights));
		Assert.Equal(new StoreCriticRating(72, 55, "Fair"), page.CriticRating);
		Assert.Equal("Shrunken Head DLC", Assert.Single(page.AddOns).Title);
	}

	[Fact]
	public void ParseProductPage_KeepsThePartsThatLoaded()
	{
		const string json = """
			{ "errors": [ { "message": "ratings unavailable" } ],
			  "data": { "RatingsPolls": null, "OpenCritic": { "productReviews": null },
			            "Product": { "sandbox": { "configuration": [ { "configs": { "supportedAudio": ["French"] } } ] } } } }
			""";

		var page = StoreCatalogParser.ParseProductPage(json);

		Assert.Null(page.PlayerRating);
		Assert.Null(page.CriticRating);
		Assert.Equal(["French"], page.AudioLanguages);
	}

	[Fact]
	public void ParseProductPage_ThrowsWhenNothingLoaded()
	{
		Assert.Throws<StoreCatalogException>(() =>
			StoreCatalogParser.ParseProductPage("""{ "errors": [ { "message": "boom" } ] }"""));
	}

	[Fact]
	public void DescriptionBlocks_KeepHeadingsTextAndPicturesInOrder()
	{
		const string markdown = """
			<!--textBlock-->
			# Features
			Fight **bosses**.
			![gif](https://cdn1.epicgames.com/a.gif)
			After the picture <img src="https://cdn1.epicgames.com/b.png"> and http ![x](http://insecure.test/c.png)
			""";

		var blocks = StoreDescriptionFormatter.ToBlocks(markdown);

		Assert.Equal(
			[
				new StoreDescriptionBlock(StoreDescriptionBlockKind.Heading, "Features", null),
				new StoreDescriptionBlock(StoreDescriptionBlockKind.Text, "Fight bosses.", null),
				new StoreDescriptionBlock(StoreDescriptionBlockKind.Image, null, "https://cdn1.epicgames.com/a.gif"),
				new StoreDescriptionBlock(StoreDescriptionBlockKind.Text, "After the picture", null),
				new StoreDescriptionBlock(StoreDescriptionBlockKind.Image, null, "https://cdn1.epicgames.com/b.png"),
				new StoreDescriptionBlock(StoreDescriptionBlockKind.Text, "and http", null)
			],
			blocks);
	}

	[Theory]
	[InlineData("SELF_REFUNDABLE", "Self-refundable")]
	[InlineData("NON_REFUNDABLE", "Non-refundable")]
	[InlineData("SOMETHING_NEW", "Something New")]
	[InlineData(null, null)]
	public void RefundType_IsShownInWords(string? refundType, string? expected)
	{
		Assert.Equal(expected, StoreProductViewModel.GetRefundText(refundType));
	}
}
