using Crimson.Core;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Crimson.ViewModels;
using Serilog;
using static Crimson.Tests.StoreCatalogTests;

namespace Crimson.Tests;

public sealed class StoreViewModelTests : IDisposable
{
	private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

	private readonly string _root = Path.Combine(Path.GetTempPath(), $"crimson-store-{Guid.NewGuid():N}");
	private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();
	private readonly FakeCatalog _catalog = new();

	[Fact]
	public async Task Discover_LoadsSectionsAndMarksOwnedGames()
	{
		_catalog.FreeGames = [Offer(title: "Free", offerId: "free", items: ["owned-item"])];
		_catalog.Search = query => Page(query.OnSaleOnly
			? [Offer(title: "Sale", offerId: "sale", items: ["sale-item"])]
			: [Offer(title: "New", offerId: "new", items: ["new-item"])]);
		var viewModel = CreateStoreViewModel(CreateLibrary("owned-item"));

		await viewModel.OnNavigatedTo(null!);
		await viewModel.ResumeAsync();

		Assert.True(viewModel.IsDiscoverVisible);
		Assert.False(viewModel.IsResultsVisible);
		Assert.Equal("Free", Assert.Single(viewModel.FreeGames).Title);
		Assert.Equal("New", Assert.Single(viewModel.NewReleases).Title);
		Assert.Equal("Sale", Assert.Single(viewModel.OnSale).Title);
		Assert.True(viewModel.FreeGames[0].IsOwned);
		Assert.Equal("owned-item", viewModel.FreeGames[0].OwnedAppName);
		Assert.False(viewModel.NewReleases[0].IsOwned);
		Assert.All(_catalog.Queries, query => Assert.Equal(StoreViewModel.SectionSize, query.Count));
	}

	[Fact]
	public async Task Discover_ShowsAnErrorOnlyWhenEverySectionFails()
	{
		_catalog.FreeGamesError = new StoreCatalogException("offline");
		_catalog.Search = query => query.OnSaleOnly
			? throw new StoreCatalogException("offline")
			: Page([Offer(title: "New")]);
		var viewModel = CreateStoreViewModel(CreateLibrary());

		await viewModel.OnNavigatedTo(null!);

		Assert.False(viewModel.ShowError);
		Assert.False(viewModel.HasFreeGames);
		Assert.True(viewModel.HasNewReleases);

		_catalog.Search = _ => throw new StoreCatalogException("offline");
		var offline = CreateStoreViewModel(CreateLibrary());
		await offline.OnNavigatedTo(null!);

		Assert.True(offline.ShowError);
		Assert.False(offline.IsDiscoverVisible);
	}

	[Fact]
	public async Task Search_PagesThroughResults()
	{
		var offers = Enumerable.Range(1, 3).Select(index => Offer(title: $"Game {index}", offerId: $"offer-{index}")).ToList();
		_catalog.Search = query => Page(offers.Skip(query.Start).Take(2).ToList(), total: offers.Count);
		var viewModel = CreateStoreViewModel(CreateLibrary());

		await viewModel.OnNavigatedTo("hades");

		Assert.Equal(StoreBrowseMode.Search, viewModel.Mode);
		Assert.True(viewModel.IsResultsVisible);
		Assert.Equal("hades", viewModel.SearchText);
		Assert.Equal("3 results", viewModel.ResultsSummary);
		Assert.Equal(2, viewModel.Results.Count);
		Assert.True(viewModel.CanLoadMore);

		await viewModel.LoadMoreAsync();

		Assert.Equal(["Game 1", "Game 2", "Game 3"], viewModel.Results.Select(item => item.Title));
		Assert.False(viewModel.CanLoadMore);
		Assert.Equal(2, _catalog.Queries[^1].Start);
		Assert.All(_catalog.Queries, query => Assert.Equal("hades", query.Keywords));
	}

	[Fact]
	public async Task Search_IgnoresAnOlderResponseThatArrivesLate()
	{
		var slow = new TaskCompletionSource<StoreSearchPage>(TaskCreationOptions.RunContinuationsAsynchronously);
		_catalog.Search = query => query.Keywords == "first"
			? slow.Task
			: Page([Offer(title: "Second result")]);
		var viewModel = CreateStoreViewModel(CreateLibrary());

		var first = viewModel.SearchAsync("first");
		await viewModel.SearchAsync("second");
		slow.SetResult(StoreSearchPageOf([Offer(title: "First result")]));
		await first;

		Assert.Equal("Second result", Assert.Single(viewModel.Results).Title);
		Assert.Equal("second", viewModel.SearchText);
	}

	[Fact]
	public async Task LoadMore_UsesTheSubmittedSearchNotTheTypedText()
	{
		_catalog.Search = query => Page([Offer(offerId: $"offer-{query.Start}")], total: 10);
		var viewModel = CreateStoreViewModel(CreateLibrary());
		await viewModel.SearchAsync("hades");

		viewModel.SearchText = "half-typed";
		await viewModel.LoadMoreAsync();

		Assert.Equal("hades", _catalog.Queries[^1].Keywords);
		Assert.Equal(2, viewModel.Results.Count);
	}

	[Fact]
	public async Task Resume_RestartsALoadInterruptedByNavigation()
	{
		var slow = new TaskCompletionSource<StoreSearchPage>(TaskCreationOptions.RunContinuationsAsynchronously);
		_catalog.Search = _ => slow.Task;
		var viewModel = CreateStoreViewModel(CreateLibrary());

		var interrupted = viewModel.SearchAsync("hades");
		viewModel.OnNavigatedFrom();
		slow.SetResult(StoreSearchPageOf([Offer(title: "Stale")]));
		await interrupted;

		Assert.True(viewModel.IsLoading);
		Assert.Empty(viewModel.Results);

		_catalog.Search = _ => Page([Offer(title: "Fresh")]);
		await viewModel.ResumeAsync();

		Assert.False(viewModel.IsLoading);
		Assert.Equal("Fresh", Assert.Single(viewModel.Results).Title);
	}

	[Fact]
	public async Task Search_ShowsErrorAndRetries()
	{
		_catalog.Search = _ => throw new StoreCatalogException("offline");
		var viewModel = CreateStoreViewModel(CreateLibrary());

		await viewModel.SearchAsync("hades");

		Assert.True(viewModel.ShowError);
		Assert.False(viewModel.IsLoading);
		Assert.False(viewModel.IsResultsVisible);

		_catalog.Search = _ => Page([Offer()]);
		await viewModel.RetryAsync();

		Assert.False(viewModel.ShowError);
		Assert.Single(viewModel.Results);
	}

	[Fact]
	public async Task ChangingSortReloadsTheCurrentList()
	{
		_catalog.Search = _ => Page([Offer()]);
		var viewModel = CreateStoreViewModel(CreateLibrary());
		await viewModel.ShowCollectionAsync(StoreBrowseMode.OnSale);
		Assert.Equal(StoreSortOrder.NewReleases, _catalog.Queries[^1].SortOrder);

		viewModel.SelectedSortIndex = viewModel.SortOptions
			.Select((option, index) => (option, index))
			.Single(entry => entry.option.Order == StoreSortOrder.PriceLowToHigh).index;

		var query = _catalog.Queries[^1];
		Assert.Equal(StoreSortOrder.PriceLowToHigh, query.SortOrder);
		Assert.True(query.OnSaleOnly);
		Assert.Equal("On sale", viewModel.ResultsTitle);
	}

	[Fact]
	public async Task Product_ResolvesSlugAndInstallsOwnedGame()
	{
		_catalog.Slugs["hades"] = new StoreOfferReference { Namespace = "min", OfferId = "offer-1" };
		_catalog.Offers["min/offer-1"] = new StoreOfferDetails
		{
			Offer = Offer(items: ["owned-item"]),
			LongDescription = "<!--text-->**Defy** the god of the dead.",
			Developer = "Supergiant Games",
			Publisher = "Supergiant Games"
		};
		var (viewModel, _) = CreateProductViewModel(CreateLibrary("owned-item"));
		Game? requested = null;
		viewModel.InstallDialogRequested += game =>
		{
			requested = game;
			return Task.CompletedTask;
		};

		await viewModel.OnNavigatedTo(new StoreProductRequest { ProductSlug = "hades", Title = "Loading title" });

		Assert.True(viewModel.IsContentVisible);
		Assert.Equal("Hades", viewModel.Title);
		Assert.Equal("Defy the god of the dead.", viewModel.Description);
		Assert.Null(viewModel.Publisher);
		Assert.Equal(StoreOwnershipState.Owned, viewModel.OwnershipState);
		Assert.Equal(StoreProductAction.Install, viewModel.PrimaryAction);
		Assert.Equal("Install", viewModel.PrimaryActionText);
		Assert.True(viewModel.CanOpenInLibrary);
		Assert.False(viewModel.ShowPrice);

		await viewModel.PrimaryActionCommand.ExecuteAsync(null);

		Assert.Equal("owned-item", requested?.AppName);
	}

	[Fact]
	public async Task Product_BuyingHandsOverToTheWebStorePage()
	{
		_catalog.Offers["min/offer-1"] = new StoreOfferDetails { Offer = Offer(price: Price(1999, 2499, "$19.99", "$24.99")) };
		var (viewModel, _) = CreateProductViewModel(CreateLibrary());
		var opened = new List<Uri>();
		viewModel.OpenWebStoreRequested += opened.Add;

		await viewModel.OnNavigatedTo(new StoreProductRequest { Namespace = "min", OfferId = "offer-1" });
		await viewModel.PrimaryActionCommand.ExecuteAsync(null);

		Assert.Equal(StoreProductAction.Buy, viewModel.PrimaryAction);
		Assert.True(viewModel.ShowPrice);
		Assert.Equal("$19.99", viewModel.PriceText);
		Assert.Equal("-20%", viewModel.DiscountText);
		Assert.Equal("https://store.epicgames.com/p/hades", Assert.Single(opened).AbsoluteUri);
	}

	[Fact]
	public async Task Product_MissingOfferFallsBackToTheWebPageOnRequest()
	{
		var (viewModel, _) = CreateProductViewModel(CreateLibrary());
		var opened = new List<Uri>();
		viewModel.OpenWebStoreRequested += opened.Add;

		await viewModel.OnNavigatedTo(new StoreProductRequest { ProductSlug = "fortnite", Title = "Fortnite" });

		Assert.True(viewModel.ShowError);
		Assert.False(viewModel.IsContentVisible);
		Assert.True(viewModel.CanOpenOnWeb);
		Assert.Empty(opened);

		viewModel.OpenOnWebCommand.Execute(null);

		Assert.Equal("https://store.epicgames.com/p/fortnite", Assert.Single(opened).AbsoluteUri);
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
			Directory.Delete(_root, recursive: true);
	}

	private StoreViewModel CreateStoreViewModel(LibraryManager library) =>
		new(_logger, _catalog, library, new ImmediateUiDispatcher(), new FixedTimeProvider(Now));

	private (StoreProductViewModel ViewModel, InstallManager Installer) CreateProductViewModel(LibraryManager library)
	{
		var storage = new Storage(_logger, _root, Path.Combine(_root, "games"));
		var installer = new InstallManager(
			_logger,
			library,
			new OwnedGamesRepository([]),
			storage,
			new DownloadManager(_logger, new HttpClient()),
			new UnusedShortcutManager(),
			new AllowInstallPermissionChecker());
		var viewModel = new StoreProductViewModel(
			_logger,
			_catalog,
			library,
			installer,
			new ImmediateUiDispatcher(),
			new FixedTimeProvider(Now));
		return (viewModel, installer);
	}

	private LibraryManager CreateLibrary(params string[] ownedCatalogItems)
	{
		var storage = new Storage(_logger, Path.Combine(_root, Guid.NewGuid().ToString("N")), Path.Combine(_root, "games"));
		var auth = new AuthManager(_logger, storage, new TestCredentialProtector(), new HttpClient());
		return new LibraryManager(_logger, new OwnedGamesRepository(ownedCatalogItems), storage, auth);
	}

	private static Task<StoreSearchPage> Page(IReadOnlyList<StoreSearchResult> results, int? total = null) =>
		Task.FromResult(StoreSearchPageOf(results, total));

	private static StoreSearchPage StoreSearchPageOf(IReadOnlyList<StoreSearchResult> results, int? total = null) =>
		new() { Results = results, Total = total ?? results.Count };

	private sealed class FakeCatalog : IStoreCatalog
	{
		public List<StoreQuery> Queries { get; } = [];
		public Func<StoreQuery, Task<StoreSearchPage>> Search { get; set; } = _ => Task.FromResult(StoreSearchPage.Empty);
		public IReadOnlyList<StoreSearchResult> FreeGames { get; set; } = [];
		public Exception? FreeGamesError { get; set; }
		public Dictionary<string, StoreOfferDetails> Offers { get; } = new();
		public Dictionary<string, StoreOfferReference> Slugs { get; } = new();

		public Task<StoreSearchPage> SearchAsync(StoreQuery query, CancellationToken cancellationToken = default)
		{
			lock (Queries)
				Queries.Add(query);
			return Search(query);
		}

		public Task<IReadOnlyList<StoreSearchResult>> GetFreeGamesAsync(CancellationToken cancellationToken = default) =>
			FreeGamesError is null ? Task.FromResult(FreeGames) : Task.FromException<IReadOnlyList<StoreSearchResult>>(FreeGamesError);

		public Task<StoreOfferDetails?> GetOfferAsync(string nameSpace, string offerId, CancellationToken cancellationToken = default) =>
			Task.FromResult(Offers.GetValueOrDefault($"{nameSpace}/{offerId}"));

		public Task<StoreOfferReference?> ResolveProductSlugAsync(string productSlug, CancellationToken cancellationToken = default) =>
			Task.FromResult(Slugs.GetValueOrDefault(productSlug));
	}

	private sealed class OwnedGamesRepository(string[] catalogItemIds) : IStoreRepository
	{
		public Task<IEnumerable<Asset>> FetchGameAssets(string platform = "Windows", string label = "Live") =>
			Task.FromResult<IEnumerable<Asset>>(catalogItemIds.Select(id => new Asset
			{
				AppName = id,
				CatalogItemId = id,
				Namespace = "min",
				BuildVersion = "1.0"
			}).ToList());

		public Task<Metadata> FetchGameMetaData(string nameSpace, string catalogItemId) =>
			Task.FromResult(new Metadata { Id = catalogItemId, Title = catalogItemId, KeyImages = [] });

		public Task<byte[]> GetGameManifest(GetManifestUrlData urlData, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public Task DownloadFileAsync(string url, string destinationPath) => throw new NotSupportedException();

		public Task<string> GetGameToken() => throw new NotSupportedException();

		public Task<byte[]?> GetOwnershipToken(string nameSpace, string catalogItemId) => throw new NotSupportedException();

		public Task<GetManifestUrlData> GetManifestUrls(
			string nameSpace,
			string catalogItem,
			string appName,
			string platform = "Windows",
			string label = "Live",
			CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class ImmediateUiDispatcher : IUiDispatcher
	{
		public bool TryEnqueue(Action callback)
		{
			callback();
			return true;
		}
	}

	private sealed class AllowInstallPermissionChecker : IInstallPermissionChecker
	{
		public InstallPermissionCheckResult Check(string folderPath) => new(true);
	}

	private sealed class UnusedShortcutManager : IGameShortcutManager
	{
		public Task CreateAsync(Game game, GameShortcutLocation location, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public void Remove(Game game)
		{
		}
	}
}
