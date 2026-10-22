using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Models;

namespace Crimson.Repository;

public interface IStoreRepository
{
	public Task<Metadata> FetchGameMetaData(string nameSpace, string catalogItemId);

	public Task<IEnumerable<Asset>> FetchGameAssets(string platform = "Windows", string label = "Live");

	public Task<IReadOnlyList<StoreSearchResult>> SearchStore(
		string query,
		CancellationToken cancellationToken = default);

	public Task<byte[]> GetGameManifest(
		GetManifestUrlData urlData,
		CancellationToken cancellationToken = default);

	public Task DownloadFileAsync(string url, string destinationPath);

	public Task<string> GetGameToken();

	public Task<byte[]?> GetOwnershipToken(string nameSpace, string catalogItemId);

	public Task<GetManifestUrlData> GetManifestUrls(
		string nameSpace,
		string catalogItem,
		string appName,
		string platform = "Windows",
		string label = "Live",
		CancellationToken cancellationToken = default);
}
