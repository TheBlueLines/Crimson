using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crimson.Core;
using Crimson.Interfaces;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Serilog;

namespace Crimson.ViewModels;

public enum StoreProductAction
{
	None,
	Play,
	Install,
	Update,
	Repair,
	Get,
	Buy,
	Redeem,
	ViewOnWeb
}

/// <summary>
/// Native product page for one store offer. Owned games install and launch from here;
/// buying, claiming and redeeming hand over to the web store.
/// </summary>
public partial class StoreProductViewModel : ObservableObject, INavigationAware
{
	private const string UnreachableMessage = "The Epic Games Store could not be reached. Check your connection and try again.";

	private readonly ILogger _log;
	private readonly IStoreCatalog _catalog;
	private readonly LibraryManager _libraryManager;
	private readonly InstallManager _installManager;
	private readonly IUiDispatcher _uiDispatcher;
	private readonly TimeProvider _time;

	private StoreProductRequest? _request;
	private StoreOfferDetails? _details;
	private Game? _ownedGame;
	private CancellationTokenSource? _loadCancellation;
	private int _loadGeneration;
	private bool _isSubscribed;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsContentVisible))]
	private bool _isLoading;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsContentVisible))]
	private bool _showError;

	[ObservableProperty]
	private string _errorMessage = string.Empty;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsContentVisible))]
	private bool _hasDetails;

	[ObservableProperty]
	private string _title = string.Empty;

	[ObservableProperty]
	private string? _coverImageUrl;

	[ObservableProperty]
	private string? _heroImageUrl;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasSummary))]
	private string _summary = string.Empty;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasDescription))]
	private string _description = string.Empty;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasDeveloper))]
	private string? _developer;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasPublisher))]
	private string? _publisher;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasReleaseDate))]
	private string? _releaseDateText;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasTags))]
	private List<string> _tags = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasGallery))]
	private List<string> _galleryImageUrls = [];

	[ObservableProperty]
	private string _priceText = string.Empty;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasOriginalPrice))]
	private string? _originalPriceText;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasDiscount))]
	private string? _discountText;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsOwned))]
	[NotifyPropertyChangedFor(nameof(ShowPrice))]
	private StoreOwnershipState _ownershipState;

	[ObservableProperty]
	private string _ownershipText = string.Empty;

	[ObservableProperty]
	private StoreProductAction _primaryAction;

	[ObservableProperty]
	private string _primaryActionText = string.Empty;

	[ObservableProperty]
	private bool _isPrimaryActionEnabled = true;

	[ObservableProperty]
	private bool _isProgressVisible;

	[ObservableProperty]
	private bool _isProgressIndeterminate;

	[ObservableProperty]
	private double _progressValue;

	[ObservableProperty]
	private bool _canOpenInLibrary;

	[ObservableProperty]
	private bool _canOpenOnWeb;

	public bool IsContentVisible => HasDetails && !IsLoading && !ShowError;
	public bool IsOwned => OwnershipState != StoreOwnershipState.NotOwned;
	public bool ShowPrice => !IsOwned && !string.IsNullOrEmpty(PriceText);
	public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
	public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
	public bool HasDeveloper => !string.IsNullOrWhiteSpace(Developer);
	public bool HasPublisher => !string.IsNullOrWhiteSpace(Publisher);
	public bool HasReleaseDate => !string.IsNullOrWhiteSpace(ReleaseDateText);
	public bool HasTags => Tags.Count > 0;
	public bool HasGallery => GalleryImageUrls.Count > 0;
	public bool HasOriginalPrice => !string.IsNullOrEmpty(OriginalPriceText);
	public bool HasDiscount => !string.IsNullOrEmpty(DiscountText);

	/// <summary>
	/// Raised for checkout, code redemption and anything else only the web store can show.
	/// </summary>
	public event Action<Uri>? OpenWebStoreRequested;

	public event Func<Game, Task>? InstallDialogRequested;

	public event Action<string>? OpenInLibraryRequested;

	public StoreProductViewModel(
		ILogger logger,
		IStoreCatalog catalog,
		LibraryManager libraryManager,
		InstallManager installManager,
		IUiDispatcher uiDispatcher)
		: this(logger, catalog, libraryManager, installManager, uiDispatcher, TimeProvider.System)
	{
	}

	internal StoreProductViewModel(
		ILogger logger,
		IStoreCatalog catalog,
		LibraryManager libraryManager,
		InstallManager installManager,
		IUiDispatcher uiDispatcher,
		TimeProvider time)
	{
		_log = logger;
		_catalog = catalog;
		_libraryManager = libraryManager;
		_installManager = installManager;
		_uiDispatcher = uiDispatcher;
		_time = time;
	}

	public Task OnNavigatedTo(object parameter)
	{
		if (parameter is not StoreProductRequest request)
			return Task.CompletedTask;

		_request = request;
		Subscribe();
		return LoadAsync();
	}

	public void OnNavigatedFrom()
	{
		CancelLoad();
		if (!_isSubscribed)
			return;

		_isSubscribed = false;
		_libraryManager.LibraryUpdated -= OnLibraryUpdated;
		_libraryManager.GameStatusUpdated -= OnGameStatusUpdated;
		_installManager.InstallationStatusChanged -= OnInstallStatusChanged;
		_installManager.InstallProgressUpdate -= OnInstallStatusChanged;
	}

	[RelayCommand]
	private Task RetryAsync() => LoadAsync();

	[RelayCommand]
	private async Task PrimaryActionAsync()
	{
		var game = _ownedGame;
		switch (PrimaryAction)
		{
			case StoreProductAction.Play when game is not null:
				_log.Information("StoreProductPage: launching {AppName}", game.AppName);
				IsPrimaryActionEnabled = false;
				try
				{
					await _libraryManager.LaunchApp(game.AppName);
				}
				finally
				{
					IsPrimaryActionEnabled = true;
				}
				break;
			case StoreProductAction.Install when game is not null:
				if (InstallDialogRequested is not null)
					await InstallDialogRequested.Invoke(game);
				break;
			case StoreProductAction.Update when game?.LocalAppState is not null:
				_installManager.AddToQueue(new InstallItem(game.AppName, ActionType.Update, game.LocalAppState.InstallPath));
				break;
			case StoreProductAction.Repair when game?.LocalAppState is not null:
				_installManager.AddToQueue(new InstallItem(game.AppName, ActionType.Repair, game.LocalAppState.InstallPath));
				break;
			case StoreProductAction.Redeem:
				RequestWebStore(StoreWebLinks.Redeem);
				break;
			case StoreProductAction.Get:
			case StoreProductAction.Buy:
				// Checkout needs Epic's payment and EULA flow, which only the web store provides.
				RequestWebStore(GetCheckoutUri());
				break;
			case StoreProductAction.ViewOnWeb:
				RequestWebStore(GetProductUri());
				break;
		}
	}

	[RelayCommand]
	private void OpenOnWeb() => RequestWebStore(GetProductUri());

	[RelayCommand]
	private void OpenInLibrary()
	{
		if (_ownedGame is not null)
			OpenInLibraryRequested?.Invoke(_ownedGame.AppName);
	}

	internal static StoreProductAction GetAction(StoreOfferDetails details, Game? ownedGame, DateTimeOffset now)
	{
		if (ownedGame is not null)
		{
			// Add-ons install with their base game, so there is nothing to do from their page.
			if (ownedGame.Metadata is null || ownedGame.IsDlc())
				return StoreProductAction.None;

			return GameInfoViewModel.GetPrimaryAction(ownedGame.LocalAppState?.InstallStatus) switch
			{
				GamePrimaryAction.Play => StoreProductAction.Play,
				GamePrimaryAction.Install => StoreProductAction.Install,
				GamePrimaryAction.Update => StoreProductAction.Update,
				GamePrimaryAction.Repair => StoreProductAction.Repair,
				_ => StoreProductAction.None
			};
		}

		if (details.IsCodeRedemptionOnly)
			return StoreProductAction.Redeem;

		if (details.Offer.EffectiveDate > now || details.Offer.Price is null)
			return StoreProductAction.ViewOnWeb;

		return details.Offer.Price.IsFree ? StoreProductAction.Get : StoreProductAction.Buy;
	}

	internal static string GetActionText(StoreProductAction action) => action switch
	{
		StoreProductAction.Play => "Play",
		StoreProductAction.Install => "Install",
		StoreProductAction.Update => "Update",
		StoreProductAction.Repair => "Repair",
		StoreProductAction.Get => "Get",
		StoreProductAction.Buy => "Buy now",
		StoreProductAction.Redeem => "Redeem code",
		StoreProductAction.ViewOnWeb => "View on Epic Games Store",
		_ => "Owned"
	};

	private async Task LoadAsync()
	{
		var request = _request;
		if (request is null)
			return;

		CancelLoad();
		var cancellation = new CancellationTokenSource();
		_loadCancellation = cancellation;
		var cancellationToken = cancellation.Token;
		var generation = Interlocked.Increment(ref _loadGeneration);

		// Show what the caller already knows while the full offer loads.
		Title = request.Title ?? string.Empty;
		CoverImageUrl = request.ImageUrl;
		CanOpenOnWeb = StoreWebLinks.ProductPage(request.ProductSlug) is not null;
		ShowError = false;
		IsLoading = true;

		try
		{
			var reference = request.HasOfferReference
				? new StoreOfferReference { Namespace = request.Namespace!, OfferId = request.OfferId! }
				: string.IsNullOrWhiteSpace(request.ProductSlug)
					? null
					: await _catalog.ResolveProductSlugAsync(request.ProductSlug, cancellationToken);
			var details = reference is null
				? null
				: await _catalog.GetOfferAsync(reference.Namespace, reference.OfferId, cancellationToken);
			var library = await LoadLibraryAsync();

			_uiDispatcher.TryEnqueue(() =>
			{
				if (generation != Volatile.Read(ref _loadGeneration))
					return;

				IsLoading = false;
				if (details is null)
				{
					// Some store pages (hubs, bundles of other products) have no catalog offer.
					SetError(CanOpenOnWeb
						? "This page isn't available in Crimson. You can still open it on the Epic Games Store."
						: "This product could not be found on the Epic Games Store.");
					return;
				}

				ApplyDetails(details);
				_ownedGame = StoreOwnership.FindOwnedGame(library, details.Offer);
				ApplyOwnership();
			});
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			_log.Warning(ex, "StoreProductViewModel: loading the offer failed");
			_uiDispatcher.TryEnqueue(() =>
			{
				if (generation != Volatile.Read(ref _loadGeneration))
					return;

				IsLoading = false;
				SetError(UnreachableMessage);
			});
		}
	}

	private async Task<IReadOnlyCollection<Game>> LoadLibraryAsync()
	{
		try
		{
			return (await _libraryManager.GetLibraryData()).ToList();
		}
		catch (Exception ex)
		{
			// The page is still useful without ownership; it just offers the web checkout.
			_log.Warning(ex, "StoreProductViewModel: could not load the library for ownership");
			return [];
		}
	}

	private void ApplyDetails(StoreOfferDetails details)
	{
		_details = details;
		var offer = details.Offer;
		var now = _time.GetUtcNow();
		var price = StorePriceText.From(offer, now);

		Title = offer.Title;
		CoverImageUrl = offer.ImageUrl ?? CoverImageUrl;
		HeroImageUrl = offer.WideImageUrl ?? details.GalleryImageUrls.FirstOrDefault() ?? offer.ImageUrl;
		Summary = offer.Description ?? string.Empty;
		var description = StoreDescriptionFormatter.ToPlainText(details.LongDescription);
		Description = string.Equals(description, Summary, StringComparison.Ordinal) ? string.Empty : description;
		Developer = details.Developer;
		Publisher = string.Equals(details.Publisher, details.Developer, StringComparison.OrdinalIgnoreCase)
			? null
			: details.Publisher;
		ReleaseDateText = details.ReleaseDate is { } releaseDate && releaseDate <= now
			? releaseDate.ToLocalTime().ToString("D", CultureInfo.CurrentCulture)
			: null;
		Tags = details.Tags.ToList();
		GalleryImageUrls = details.GalleryImageUrls.ToList();
		PriceText = price.Current;
		OriginalPriceText = price.Original;
		DiscountText = price.Discount;
		CanOpenOnWeb = GetProductUri() is not null;
		HasDetails = true;
		OnPropertyChanged(nameof(ShowPrice));
	}

	private void ApplyOwnership()
	{
		if (_details is null)
			return;

		OwnershipState = StoreOwnership.GetState(_ownedGame);
		OwnershipText = OwnershipState switch
		{
			StoreOwnershipState.Installed => "Installed",
			StoreOwnershipState.Owned => "In your library",
			_ => string.Empty
		};
		CanOpenInLibrary = _ownedGame is { Metadata: not null } && !_ownedGame.IsDlc();

		var install = _installManager.CurrentInstall;
		if (_ownedGame is not null && install is not null &&
			string.Equals(install.AppName, _ownedGame.AppName, StringComparison.Ordinal) &&
			IsActionRunning(install))
		{
			ApplyInstallProgress(install);
			return;
		}

		PrimaryAction = GetAction(_details, _ownedGame, _time.GetUtcNow());
		PrimaryActionText = GetActionText(PrimaryAction);
		IsPrimaryActionEnabled = PrimaryAction != StoreProductAction.None;
		IsProgressVisible = false;
	}

	private void ApplyInstallProgress(InstallItem install)
	{
		PrimaryAction = StoreProductAction.None;
		IsPrimaryActionEnabled = false;
		IsProgressVisible = true;
		IsProgressIndeterminate = install.Status is ActionStatus.Pending or ActionStatus.Cancelling;
		ProgressValue = install.ProgressPercentage;
		PrimaryActionText = install.Status switch
		{
			ActionStatus.Pending => "Pending...",
			ActionStatus.Paused => $"Paused at {install.ProgressPercentage}%",
			ActionStatus.Cancelling => "Cancelling...",
			_ => $"{install.ProgressPercentage}%"
		};
	}

	private void SetError(string message)
	{
		ErrorMessage = message;
		ShowError = true;
	}

	private void RequestWebStore(Uri? uri)
	{
		if (uri is not null && EpicEndpointPolicy.IsAllowedStoreUri(uri))
			OpenWebStoreRequested?.Invoke(uri);
	}

	private Uri? GetProductUri() =>
		StoreWebLinks.ProductPage(_details?.Offer.ProductSlug ?? _request?.ProductSlug);

	private Uri? GetCheckoutUri()
	{
		// The product page carries Epic's own Get / Buy button, which runs checkout with the
		// shared session. The direct purchase link is only for offers without a page.
		var offer = _details?.Offer;
		return GetProductUri() ?? StoreWebLinks.Purchase(offer?.Namespace, offer?.OfferId);
	}

	private void Subscribe()
	{
		if (_isSubscribed)
			return;

		_isSubscribed = true;
		_libraryManager.LibraryUpdated += OnLibraryUpdated;
		_libraryManager.GameStatusUpdated += OnGameStatusUpdated;
		_installManager.InstallationStatusChanged += OnInstallStatusChanged;
		_installManager.InstallProgressUpdate += OnInstallStatusChanged;
	}

	private void CancelLoad()
	{
		Interlocked.Increment(ref _loadGeneration);
		var cancellation = _loadCancellation;
		_loadCancellation = null;
		cancellation?.Cancel();
		cancellation?.Dispose();
	}

	private void OnLibraryUpdated(IEnumerable<Game> games)
	{
		if (games is null)
			return;

		var snapshot = games.ToList();
		_uiDispatcher.TryEnqueue(() =>
		{
			if (_details is null)
				return;

			_ownedGame = StoreOwnership.FindOwnedGame(snapshot, _details.Offer);
			ApplyOwnership();
		});
	}

	private void OnGameStatusUpdated(Game game)
	{
		if (game is null)
			return;

		_uiDispatcher.TryEnqueue(() =>
		{
			if (_ownedGame is null || !string.Equals(game.AppName, _ownedGame.AppName, StringComparison.Ordinal))
				return;

			_ownedGame = game;
			ApplyOwnership();
		});
	}

	private void OnInstallStatusChanged(InstallItem install)
	{
		if (install is null)
			return;

		_uiDispatcher.TryEnqueue(() =>
		{
			if (_ownedGame is null || !string.Equals(install.AppName, _ownedGame.AppName, StringComparison.Ordinal))
				return;

			if (IsActionRunning(install))
			{
				ApplyInstallProgress(install);
				return;
			}

			_ownedGame = _libraryManager.GetGameInfo(_ownedGame.AppName) ?? _ownedGame;
			ApplyOwnership();
		});
	}

	private static bool IsActionRunning(InstallItem install) =>
		install.Status is ActionStatus.Pending or ActionStatus.OnGoing or ActionStatus.Processing
			or ActionStatus.Paused or ActionStatus.Cancelling;
}
