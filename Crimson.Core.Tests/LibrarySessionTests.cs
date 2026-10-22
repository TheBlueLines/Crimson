using Crimson.Core;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Serilog;

namespace Crimson.Tests;

public sealed class LibrarySessionTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"crimson-library-session-{Guid.NewGuid():N}");
	private readonly Serilog.Core.Logger _logger = new LoggerConfiguration().CreateLogger();
	private readonly HttpClient _http = new();

	[Fact]
	public async Task LogoutDiscardsOldMetadataAndForcesNewAccountRefresh()
	{
		var storage = new Storage(_logger, _root, Path.Combine(_root, "games"));
		var repository = new ControlledRepository();
		var auth = new AuthManager(_logger, storage, new TestCredentialProtector(), _http);
		var library = new LibraryManager(_logger, repository, storage, auth);
		var published = new List<string>();
		library.LibraryUpdated += games => published.AddRange(games.Select(game => game.AppName));

		var first = library.GetLibraryData();
		await repository.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await auth.Logout();
		repository.Account = "second";
		var second = library.GetLibraryData();
		repository.FirstMetadata.SetResult(ControlledRepository.Metadata("first"));

		await first.WaitAsync(TimeSpan.FromSeconds(5));
		var games = (await second.WaitAsync(TimeSpan.FromSeconds(5))).ToList();
		Assert.Equal(["second"], games.Select(game => game.AppName));
		Assert.DoesNotContain("first", published);
		Assert.False(File.Exists(Path.Combine(_root, "metadata", "first.json")));
		Assert.DoesNotContain("first", storage.GameMetaDataDictionary.Keys);
		Assert.Equal(2, repository.AssetRequests);
	}

	[Fact]
	public async Task AuthenticationFailureDiscardsInFlightRefresh()
	{
		var storage = new Storage(_logger, _root, Path.Combine(_root, "games"));
		var repository = new ControlledRepository();
		var auth = new AuthManager(_logger, storage, new TestCredentialProtector(), _http);
		var library = new LibraryManager(_logger, repository, storage, auth);
		var published = new List<string>();
		library.LibraryUpdated += games => published.AddRange(games.Select(game => game.AppName));

		var refresh = library.GetLibraryData();
		await repository.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(AuthenticationStatus.LoggedOut, await auth.CheckAuthStatus());
		repository.FirstMetadata.SetResult(ControlledRepository.Metadata("first"));

		Assert.Empty(await refresh.WaitAsync(TimeSpan.FromSeconds(5)));
		Assert.Empty(published);
		Assert.False(File.Exists(Path.Combine(_root, "metadata", "first.json")));
	}

	[Fact]
	public async Task LogoutAfterCompletedRefreshClearsCachedOwnership()
	{
		var storage = new Storage(_logger, _root, Path.Combine(_root, "games"));
		var repository = new ControlledRepository { Account = "second" };
		var auth = new AuthManager(_logger, storage, new TestCredentialProtector(), _http);
		var library = new LibraryManager(_logger, repository, storage, auth);
		await library.GetLibraryData();
		await auth.Logout();
		repository.Account = "third";

		var games = await library.GetLibraryData();

		Assert.Equal(["third"], games.Select(game => game.AppName));
		Assert.Equal(2, repository.AssetRequests);
	}

	public void Dispose()
	{
		_http.Dispose();
		_logger.Dispose();
		if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
	}

	private sealed class ControlledRepository : IStoreRepository
	{
		public string Account = "first";
		public int AssetRequests;
		public TaskCompletionSource<bool> FirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource<Metadata> FirstMetadata { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<IEnumerable<Asset>> FetchGameAssets(string platform = "Windows", string label = "Live")
		{
			AssetRequests++;
			return Task.FromResult<IEnumerable<Asset>>([new Asset {
				AppName = Account, Namespace = "synthetic", CatalogItemId = Account, BuildVersion = "1.0"
			}]);
		}

		public Task<Metadata> FetchGameMetaData(string nameSpace, string catalogItemId)
		{
			if (catalogItemId != "first") return Task.FromResult(Metadata(catalogItemId));
			FirstRequest.TrySetResult(true);
			return FirstMetadata.Task;
		}

		public static Metadata Metadata(string app) => new() { Id = app, Title = app, KeyImages = [] };
		public Task<byte[]> GetGameManifest(GetManifestUrlData data, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task DownloadFileAsync(string url, string destinationPath) => throw new NotSupportedException();
		public Task<string> GetGameToken() => throw new NotSupportedException();
		public Task<byte[]?> GetOwnershipToken(string nameSpace, string catalogItemId) => throw new NotSupportedException();
		public Task<GetManifestUrlData> GetManifestUrls(string nameSpace, string catalogItem, string appName, string platform = "Windows", string label = "Live", CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}
}
