using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Core;
using Crimson.Models;
using Serilog;

namespace Crimson.Repository;

/// <summary>
/// Queries the same GraphQL endpoint the Epic Games Launcher uses for its store.
/// None of these queries need the user's session.
/// </summary>
internal sealed class EpicStoreCatalog : IStoreCatalog
{
	private const string GraphQlUrl = "https://launcher.store.epicgames.com/graphql";
	private const string FreeGamesUrl = "https://store-site-backend-static-ipv4.ak.epicgames.com/freeGamesPromotions";

	private const string OfferFields = """
		title
		id
		namespace
		description
		offerType
		effectiveDate
		seller { name }
		keyImages { type url }
		productSlug
		urlSlug
		catalogNs { mappings(pageType: "productHome") { pageSlug } }
		offerMappings { pageSlug }
		items { id namespace }
		price(country: $country) {
			totalPrice {
				discountPrice
				originalPrice
				currencyCode
				fmtPrice(locale: $locale) { originalPrice discountPrice }
			}
		}
		""";

	private const string SearchQuery = $$"""
		query searchStoreQuery(
			$count: Int,
			$start: Int,
			$country: String!,
			$locale: String,
			$keywords: String,
			$category: String,
			$sortBy: String,
			$sortDir: String,
			$onSale: Boolean,
			$effectiveDate: String
		) {
			Catalog {
				searchStore(
					count: $count,
					start: $start,
					country: $country,
					locale: $locale,
					keywords: $keywords,
					category: $category,
					sortBy: $sortBy,
					sortDir: $sortDir,
					onSale: $onSale,
					effectiveDate: $effectiveDate
				) {
					elements {
						{{OfferFields}}
					}
					paging { count total }
				}
			}
		}
		""";

	private const string OfferQuery = $$"""
		query getCatalogOffer($namespace: String!, $id: String!, $country: String!, $locale: String) {
			Catalog {
				catalogOffer(namespace: $namespace, id: $id, locale: $locale) {
					{{OfferFields}}
					longDescription
					releaseDate
					isCodeRedemptionOnly
					refundType
					developerDisplayName
					publisherDisplayName
					customAttributes { key value }
					tags { name groupName }
				}
			}
		}
		""";

	// Everything else the web product page shows, in one request. Epic resolves each part on its
	// own, so a product without ratings or a critic score still returns the rest.
	private const string ProductPageQuery = $$"""
		query getProductPage($sandboxId: String!, $locale: String!, $country: String!, $sku: String!) {
			Product {
				sandbox(sandboxId: $sandboxId) {
					configuration(locale: $locale) {
						... on StoreConfiguration {
							configs {
								technicalRequirements { windows { title minimum recommended } }
								socialLinks { platform url }
								supportedAudio
								supportedText
							}
						}
						... on HomeConfiguration {
							configs {
								keyImages { ... on KeyImage { type url } }
								longDescription
							}
						}
					}
				}
			}
			RatingsPolls {
				getProductResult(sandboxId: $sandboxId, locale: $locale) {
					averageRating
					pollResult { localizations { resultTitle resultText } }
				}
			}
			OpenCritic {
				productReviews(sku: $sku) { openCriticScore percentRecommended award }
			}
			Catalog {
				searchStore(
					namespace: $sandboxId,
					category: "addons|digitalextras",
					count: 40,
					country: $country,
					locale: $locale,
					sortBy: "releaseDate",
					sortDir: "DESC"
				) {
					elements {
						{{OfferFields}}
					}
				}
			}
		}
		""";

	private const string PageMappingQuery = """
		query getMappingByPageSlug($pageSlug: String!) {
			StorePageMapping {
				mapping(pageSlug: $pageSlug) {
					sandboxId
					mappings { offerId }
				}
			}
		}
		""";

	private readonly ILogger _log;
	private readonly HttpClient _storeClient;
	private readonly Func<StoreRegion> _region;
	private readonly TimeProvider _time;

	public EpicStoreCatalog(
		ILogger logger,
		HttpClient storeClient,
		Func<StoreRegion>? region = null,
		TimeProvider? time = null)
	{
		_log = logger;
		_storeClient = storeClient;
		_region = region ?? (() => StoreRegion.Current);
		_time = time ?? TimeProvider.System;
	}

	public async Task<StoreSearchPage> SearchAsync(StoreQuery query, CancellationToken cancellationToken = default)
	{
		var variables = query.ToVariables(_region(), _time.GetUtcNow());
		var json = await PostAsync(SearchQuery, variables, cancellationToken);
		return StoreCatalogParser.ParseSearchPage(json);
	}

	public async Task<IReadOnlyList<StoreSearchResult>> GetFreeGamesAsync(CancellationToken cancellationToken = default)
	{
		var region = _region();
		var country = Uri.EscapeDataString(region.Country);
		var uri = EpicEndpointPolicy.RequireStoreApiUri(
			$"{FreeGamesUrl}?locale={Uri.EscapeDataString(region.Locale)}&country={country}&allowCountries={country}");
		var json = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, uri), cancellationToken);
		var now = _time.GetUtcNow();

		// The feed also carries ordinary discounts; only keep the giveaways.
		return StoreCatalogParser.ParseSearchPage(json).Results
			.Where(offer => IsFreeNow(offer, now) || IsFreeSoon(offer, now))
			.OrderBy(offer => IsFreeNow(offer, now) ? 0 : 1)
			.ThenBy(offer => offer.CurrentPromotion?.EndDate ?? offer.UpcomingPromotion?.StartDate)
			.ToList();
	}

	public async Task<StoreOfferDetails?> GetOfferAsync(
		string nameSpace,
		string offerId,
		CancellationToken cancellationToken = default)
	{
		var region = _region();
		var json = await PostAsync(
			OfferQuery,
			new Dictionary<string, object?>
			{
				["namespace"] = nameSpace,
				["id"] = offerId,
				["country"] = region.Country,
				["locale"] = region.Locale
			},
			cancellationToken);
		return StoreCatalogParser.ParseOfferDetails(json);
	}

	public async Task<StoreProductPageInfo> GetProductPageAsync(
		string sandboxId,
		string? productSlug,
		CancellationToken cancellationToken = default)
	{
		var region = _region();
		var json = await PostAsync(
			ProductPageQuery,
			new Dictionary<string, object?>
			{
				["sandboxId"] = sandboxId,
				["locale"] = region.Locale,
				["country"] = region.Country,
				// OpenCritic keys Epic products by their page slug; an unknown key just returns null.
				["sku"] = string.IsNullOrWhiteSpace(productSlug) ? string.Empty : $"EPIC_{productSlug.Trim('/')}"
			},
			cancellationToken);
		return StoreCatalogParser.ParseProductPage(json);
	}

	public async Task<StoreOfferReference?> ResolveProductSlugAsync(
		string productSlug,
		CancellationToken cancellationToken = default)
	{
		var json = await PostAsync(
			PageMappingQuery,
			new Dictionary<string, object?> { ["pageSlug"] = productSlug.Trim('/') },
			cancellationToken);
		return StoreCatalogParser.ParsePageMapping(json);
	}

	internal static bool IsFreeNow(StoreSearchResult offer, DateTimeOffset now) =>
		offer.CurrentPromotion is { IsFree: true } promotion && promotion.IsActive(now);

	internal static bool IsFreeSoon(StoreSearchResult offer, DateTimeOffset now) =>
		offer.UpcomingPromotion is { IsFree: true } promotion && promotion.StartDate > now;

	private Task<string> PostAsync(
		string query,
		IReadOnlyDictionary<string, object?> variables,
		CancellationToken cancellationToken)
	{
		var uri = EpicEndpointPolicy.RequireStoreApiUri(GraphQlUrl);
		var payload = JsonSerializer.Serialize(new { query, variables });
		return SendAsync(
			() => new HttpRequestMessage(HttpMethod.Post, uri)
			{
				Content = new StringContent(payload, Encoding.UTF8, "application/json")
			},
			cancellationToken);
	}

	private async Task<string> SendAsync(
		Func<HttpRequestMessage> createRequest,
		CancellationToken cancellationToken)
	{
		try
		{
			using var request = createRequest();
			using var response = await _storeClient.SendAsync(request, cancellationToken);
			if (!response.IsSuccessStatusCode)
			{
				_log.Warning(
					"EpicStoreCatalog: {Host} returned HTTP {StatusCode}",
					request.RequestUri?.Host,
					(int)response.StatusCode);
				throw new StoreCatalogException($"The store returned HTTP {(int)response.StatusCode}.");
			}

			return await response.Content.ReadAsStringAsync(cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (StoreCatalogException)
		{
			throw;
		}
		catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
		{
			_log.Warning("EpicStoreCatalog: request failed with {ErrorType}", ex.GetType().Name);
			throw new StoreCatalogException("The store could not be reached.", ex);
		}
	}
}
