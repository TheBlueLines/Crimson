using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Models;

namespace Crimson.Repository;

/// <summary>
/// Read-only access to the public Epic Games Store catalog. Methods throw
/// <see cref="StoreCatalogException"/> when the store cannot be reached and return
/// null when the requested offer does not exist.
/// </summary>
public interface IStoreCatalog
{
	public Task<StoreSearchPage> SearchAsync(StoreQuery query, CancellationToken cancellationToken = default);

	/// <summary>
	/// Offers in Epic's current and upcoming free-games promotion.
	/// </summary>
	public Task<IReadOnlyList<StoreSearchResult>> GetFreeGamesAsync(CancellationToken cancellationToken = default);

	public Task<StoreOfferDetails?> GetOfferAsync(
		string nameSpace,
		string offerId,
		CancellationToken cancellationToken = default);

	public Task<StoreOfferReference?> ResolveProductSlugAsync(
		string productSlug,
		CancellationToken cancellationToken = default);
}
