using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crimson.Core;
using Crimson.Interfaces;
using Crimson.Models;
using Crimson.Repository;
using Serilog;

namespace Crimson.ViewModels;

public enum StoreBrowseMode
{
	Discover,
	Search,
	NewReleases,
	OnSale
}

public sealed record StoreSortOption(string Name, StoreSortOrder Order);

/// <summary>
/// Native Epic Games Store front: featured sections when browsing, and a paged
/// grid for searches and "see all" lists.
/// </summary>
public partial class StoreViewModel : ObservableObject, INavigationAware
{
	internal const int SectionSize = 12;
	internal const int PageSize = 40;
	private static readonly TimeSpan DiscoverLifetime = TimeSpan.FromMinutes(10);

	private readonly ILogger _log;
	private readonly IStoreCatalog _catalog;
	private readonly LibraryManager _libraryManager;
	private readonly IUiDispatcher _uiDispatcher;
	private readonly TimeProvider _time;

	private IReadOnlyCollection<Game> _library = [];
	// The submitted search, as opposed to SearchText, which follows the box while typing.
	private string _activeKeywords = string.Empty;
	private CancellationTokenSource? _requestCancellation;
	private int _requestGeneration;
	private int _resultsTotal;
	private DateTimeOffset _discoverLoadedAt = DateTimeOffset.MinValue;
	private bool _suppressSortReload;
	private bool _isSubscribed;

	public IReadOnlyList<StoreSortOption> SortOptions { get; } =
	[
		new("Relevance", StoreSortOrder.Relevance),
		new("New releases", StoreSortOrder.NewReleases),
		new("Price: low to high", StoreSortOrder.PriceLowToHigh),
		new("Price: high to low", StoreSortOrder.PriceHighToLow),
		new("Title: A to Z", StoreSortOrder.Title)
	];

	public ObservableCollection<StoreItem> Results { get; } = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasFreeGames))]
	private List<StoreItem> _freeGames = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasNewReleases))]
	private List<StoreItem> _newReleases = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasOnSale))]
	private List<StoreItem> _onSale = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsDiscoverVisible))]
	[NotifyPropertyChangedFor(nameof(IsResultsVisible))]
	private StoreBrowseMode _mode = StoreBrowseMode.Discover;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsDiscoverVisible))]
	[NotifyPropertyChangedFor(nameof(IsResultsVisible))]
	private bool _isLoading;

	[ObservableProperty]
	private bool _isLoadingMore;

	[ObservableProperty]
	private bool _canLoadMore;

	[ObservableProperty]
	private bool _showEmptyState;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsDiscoverVisible))]
	[NotifyPropertyChangedFor(nameof(IsResultsVisible))]
	private bool _showError;

	[ObservableProperty]
	private string _errorMessage = string.Empty;

	[ObservableProperty]
	private string _resultsTitle = string.Empty;

	[ObservableProperty]
	private string _resultsSummary = string.Empty;

	[ObservableProperty]
	private string _searchText = string.Empty;

	[ObservableProperty]
	private int _selectedSortIndex;

	public bool HasFreeGames => FreeGames.Count > 0;
	public bool HasNewReleases => NewReleases.Count > 0;
	public bool HasOnSale => OnSale.Count > 0;
	public bool IsDiscoverVisible => Mode == StoreBrowseMode.Discover && !IsLoading && !ShowError;
	public bool IsResultsVisible => Mode != StoreBrowseMode.Discover && !ShowError;

	public StoreViewModel(
		ILogger logger,
		IStoreCatalog catalog,
		LibraryManager libraryManager,
		IUiDispatcher uiDispatcher)
		: this(logger, catalog, libraryManager, uiDispatcher, TimeProvider.System)
	{
	}

	internal StoreViewModel(
		ILogger logger,
		IStoreCatalog catalog,
		LibraryManager libraryManager,
		IUiDispatcher uiDispatcher,
		TimeProvider time)
	{
		_log = logger;
		_catalog = catalog;
		_libraryManager = libraryManager;
		_uiDispatcher = uiDispatcher;
		_time = time;
	}

	/// <summary>
	/// A string parameter opens search results for it; anything else shows the store front.
	/// </summary>
	public Task OnNavigatedTo(object parameter)
	{
		Subscribe();
		_ = RefreshOwnershipAsync();
		return parameter is string query && !string.IsNullOrWhiteSpace(query)
			? SearchAsync(query)
			: ShowDiscoverAsync();
	}

	/// <summary>
	/// Back navigation to a cached page keeps the results and scroll position;
	/// only ownership can have changed, for example after a purchase.
	/// </summary>
	public Task ResumeAsync()
	{
		Subscribe();

		// Leaving mid-load cancelled the request, so pick it up again.
		return IsLoading
			? Task.WhenAll(RefreshOwnershipAsync(), RetryAsync())
			: RefreshOwnershipAsync();
	}

	public void OnNavigatedFrom()
	{
		CancelRequest();
		IsLoadingMore = false;
		if (!_isSubscribed)
			return;

		_isSubscribed = false;
		_libraryManager.LibraryUpdated -= OnLibraryUpdated;
		_libraryManager.GameStatusUpdated -= OnGameStatusUpdated;
	}

	[RelayCommand]
	public async Task SearchAsync(string? query)
	{
		var keywords = query?.Trim() ?? string.Empty;
		if (keywords.Length == 0)
		{
			await ShowDiscoverAsync();
			return;
		}

		SearchText = keywords;
		_activeKeywords = keywords;
		SetSortSilently(StoreSortOrder.Relevance);
		ResultsTitle = $"Results for “{keywords}”";
		await LoadResultsAsync(StoreBrowseMode.Search);
	}

	[RelayCommand]
	private Task ShowNewReleasesAsync() => ShowCollectionAsync(StoreBrowseMode.NewReleases);

	[RelayCommand]
	private Task ShowOnSaleAsync() => ShowCollectionAsync(StoreBrowseMode.OnSale);

	public Task ShowCollectionAsync(StoreBrowseMode mode)
	{
		if (mode is StoreBrowseMode.Discover or StoreBrowseMode.Search)
			return ShowDiscoverAsync();

		SearchText = string.Empty;
		SetSortSilently(StoreSortOrder.NewReleases);
		ResultsTitle = mode == StoreBrowseMode.OnSale ? "On sale" : "New releases";
		return LoadResultsAsync(mode);
	}

	[RelayCommand]
	public async Task ShowDiscoverAsync()
	{
		SearchText = string.Empty;
		var stale = _time.GetUtcNow() - _discoverLoadedAt > DiscoverLifetime;
		var generation = BeginRequest(StoreBrowseMode.Discover, out var cancellationToken);
		Results.Clear();
		if (!stale && !ShowError)
		{
			IsLoading = false;
			return;
		}

		IsLoading = true;
		var now = _time.GetUtcNow();
		var freeGames = LoadSectionAsync(() => _catalog.GetFreeGamesAsync(cancellationToken));
		var newReleases = LoadSectionAsync(async () => (await _catalog.SearchAsync(
			new StoreQuery { SortOrder = StoreSortOrder.NewReleases, Count = SectionSize },
			cancellationToken)).Results);
		var onSale = LoadSectionAsync(async () => (await _catalog.SearchAsync(
			new StoreQuery { SortOrder = StoreSortOrder.NewReleases, OnSaleOnly = true, Count = SectionSize },
			cancellationToken)).Results);
		await Task.WhenAll(freeGames, newReleases, onSale);
		if (!IsCurrent(generation))
			return;

		// Building the item lists is CPU work; keep it off the UI thread and only
		// assign the finished collections inside the dispatcher callback.
		var freeResult = await freeGames;
		var newReleaseResult = await newReleases;
		var onSaleResult = await onSale;
		var freeItems = CreateItems(freeResult ?? [], now);
		var newReleaseItems = CreateItems(newReleaseResult ?? [], now);
		var onSaleItems = CreateItems(onSaleResult ?? [], now);

		_uiDispatcher.TryEnqueue(() =>
		{
			if (!IsCurrent(generation))
				return;

			FreeGames = freeItems;
			NewReleases = newReleaseItems;
			OnSale = onSaleItems;
			IsLoading = false;

			if (freeResult is null && newReleaseResult is null && onSaleResult is null)
			{
				SetError("The Epic Games Store could not be reached. Check your connection and try again.");
				return;
			}

			_discoverLoadedAt = now;
			ShowEmptyState = !HasFreeGames && !HasNewReleases && !HasOnSale;
		});
	}

	[RelayCommand]
	public async Task LoadMoreAsync()
	{
		if (!CanLoadMore || IsLoadingMore || Mode == StoreBrowseMode.Discover)
			return;

		var generation = Volatile.Read(ref _requestGeneration);
		var cancellationToken = _requestCancellation?.Token ?? CancellationToken.None;
		IsLoadingMore = true;
		try
		{
			var page = await _catalog.SearchAsync(CreateQuery(Mode, Results.Count), cancellationToken);
			_uiDispatcher.TryEnqueue(() =>
			{
				if (!IsCurrent(generation))
					return;

				AppendResults(page);
				IsLoadingMore = false;
			});
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			_log.Warning(ex, "StoreViewModel: loading more results failed");
			_uiDispatcher.TryEnqueue(() =>
			{
				if (IsCurrent(generation))
					IsLoadingMore = false;
			});
		}
	}

	[RelayCommand]
	public Task RetryAsync()
	{
		return Mode switch
		{
			StoreBrowseMode.Search => SearchAsync(_activeKeywords),
			StoreBrowseMode.Discover => ShowDiscoverAsync(),
			_ => LoadResultsAsync(Mode)
		};
	}

	partial void OnSelectedSortIndexChanged(int value)
	{
		if (_suppressSortReload || Mode == StoreBrowseMode.Discover || value < 0 || value >= SortOptions.Count)
			return;

		_ = LoadResultsAsync(Mode);
	}

	private async Task LoadResultsAsync(StoreBrowseMode mode)
	{
		var generation = BeginRequest(mode, out var cancellationToken);
		Results.Clear();
		ResultsSummary = string.Empty;
		IsLoading = true;

		try
		{
			var page = await _catalog.SearchAsync(CreateQuery(mode, start: 0), cancellationToken);
			_uiDispatcher.TryEnqueue(() =>
			{
				if (!IsCurrent(generation))
					return;

				_resultsTotal = page.Total;
				AppendResults(page);
				IsLoading = false;
				ShowEmptyState = Results.Count == 0;
				ResultsSummary = mode != StoreBrowseMode.Search
					? string.Empty
					: (page.Total == 1 ? "1 result" : $"{page.Total:N0} results");
			});
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			_log.Warning(ex, "StoreViewModel: loading {Mode} failed", mode);
			_uiDispatcher.TryEnqueue(() =>
			{
				if (!IsCurrent(generation))
					return;

				IsLoading = false;
				SetError("The Epic Games Store could not be reached. Check your connection and try again.");
			});
		}
	}

	private StoreQuery CreateQuery(StoreBrowseMode mode, int start)
	{
		var sort = SelectedSortIndex >= 0 && SelectedSortIndex < SortOptions.Count
			? SortOptions[SelectedSortIndex].Order
			: StoreSortOrder.Relevance;
		return new StoreQuery
		{
			Keywords = mode == StoreBrowseMode.Search ? _activeKeywords : null,
			SortOrder = sort,
			OnSaleOnly = mode == StoreBrowseMode.OnSale,
			Start = start,
			Count = PageSize
		};
	}

	private void AppendResults(StoreSearchPage page)
	{
		var now = _time.GetUtcNow();
		var known = Results
			.Select(item => item.Offer.OfferId)
			.Where(id => id is not null)
			.ToHashSet(StringComparer.Ordinal);
		foreach (var offer in page.Results)
		{
			// Paging over a live catalog can repeat an offer that moved between pages.
			if (offer.OfferId is not null && !known.Add(offer.OfferId))
				continue;

			Results.Add(CreateItem(offer, now));
		}

		CanLoadMore = page.Results.Count > 0 && Results.Count < _resultsTotal;
	}

	private int BeginRequest(StoreBrowseMode mode, out CancellationToken cancellationToken)
	{
		CancelRequest();
		var cancellation = new CancellationTokenSource();
		_requestCancellation = cancellation;
		cancellationToken = cancellation.Token;

		Mode = mode;
		ShowError = false;
		ErrorMessage = string.Empty;
		ShowEmptyState = false;
		CanLoadMore = false;
		IsLoadingMore = false;
		_resultsTotal = 0;
		return Interlocked.Increment(ref _requestGeneration);
	}

	private void CancelRequest()
	{
		Interlocked.Increment(ref _requestGeneration);
		var cancellation = _requestCancellation;
		_requestCancellation = null;
		cancellation?.Cancel();
		cancellation?.Dispose();
	}

	private bool IsCurrent(int generation) => generation == Volatile.Read(ref _requestGeneration);

	private void SetError(string message)
	{
		ErrorMessage = message;
		ShowError = true;
		ShowEmptyState = false;
	}

	private void SetSortSilently(StoreSortOrder order)
	{
		_suppressSortReload = true;
		try
		{
			var index = 0;
			for (var candidate = 0; candidate < SortOptions.Count; candidate++)
			{
				if (SortOptions[candidate].Order != order)
					continue;
				index = candidate;
				break;
			}

			SelectedSortIndex = index;
		}
		finally
		{
			_suppressSortReload = false;
		}
	}

	private async Task<IReadOnlyList<StoreSearchResult>?> LoadSectionAsync(
		Func<Task<IReadOnlyList<StoreSearchResult>>> load)
	{
		try
		{
			return await load();
		}
		catch (OperationCanceledException)
		{
			return null;
		}
		catch (Exception ex)
		{
			// One section failing should not take the whole store front down.
			_log.Warning(ex, "StoreViewModel: a store section failed to load");
			return null;
		}
	}

	private List<StoreItem> CreateItems(IEnumerable<StoreSearchResult> offers, DateTimeOffset now) =>
		offers.Select(offer => CreateItem(offer, now)).ToList();

	private StoreItem CreateItem(StoreSearchResult offer, DateTimeOffset now)
	{
		var item = StoreItem.Create(offer, now);
		item.ApplyOwnership(_library);
		return item;
	}

	private void Subscribe()
	{
		if (_isSubscribed)
			return;

		_isSubscribed = true;
		_libraryManager.LibraryUpdated += OnLibraryUpdated;
		_libraryManager.GameStatusUpdated += OnGameStatusUpdated;
	}

	private async Task RefreshOwnershipAsync()
	{
		try
		{
			var games = (await _libraryManager.GetLibraryData()).ToList();
			_uiDispatcher.TryEnqueue(() => ApplyLibrary(games));
		}
		catch (Exception ex)
		{
			_log.Warning(ex, "StoreViewModel: could not load the library for ownership");
		}
	}

	private void OnLibraryUpdated(IEnumerable<Game> games)
	{
		if (games is null)
			return;

		var snapshot = games.ToList();
		_uiDispatcher.TryEnqueue(() => ApplyLibrary(snapshot));
	}

	private void OnGameStatusUpdated(Game game)
	{
		if (game is null)
			return;

		_uiDispatcher.TryEnqueue(() =>
		{
			var games = _library
				.Where(candidate => !string.Equals(candidate.AppName, game.AppName, StringComparison.Ordinal))
				.Append(game)
				.ToList();
			ApplyLibrary(games);
		});
	}

	private void ApplyLibrary(IReadOnlyCollection<Game> games)
	{
		_library = games;
		foreach (var item in FreeGames.Concat(NewReleases).Concat(OnSale).Concat(Results))
			item.ApplyOwnership(games);
	}
}
