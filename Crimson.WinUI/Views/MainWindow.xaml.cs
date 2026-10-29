using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Core;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Serilog;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Crimson.Views;

/// <summary>
///	 Main Window
/// </summary>
public sealed partial class MainWindow : Window
{
	private const int GameIconBoxHeight = 36;

	private static readonly string[] ProgressBackgroundKeys =
	[
		"NavigationViewItemBackground",
		"NavigationViewItemBackgroundPointerOver",
		"NavigationViewItemBackgroundPressed",
		"NavigationViewItemBackgroundSelected",
		"NavigationViewItemBackgroundSelectedPointerOver",
		"NavigationViewItemBackgroundSelectedPressed"
	];

	// Only the docked pane is see-through; the overlay pane (compact/minimal) keeps its acrylic
	// so it stays readable over the page it covers.
	private static readonly string[] PaneBackgroundKeys =
	[
		"NavigationViewExpandedPaneBackground"
	];

	private const string GameNavTagPrefix = "game:";

	public bool IsLoggedIn;
	private ILogger _log = App.GetService<ILogger>();
	private readonly AuthManager _authManager;
	private readonly InstallManager _installManager;
	private readonly LibraryManager _libraryManager;
	private readonly IStoreCatalog _storeCatalog;
	private readonly SettingsManager _settingsManager;

	private List<Game> _libraryCache = new();
	private CancellationTokenSource? _storeSearchCancellation;
	private string? _currentGameAppName;

	private Brush? _contentBackground;
	private readonly SolidColorBrush _transparentChromeBrush = new(Microsoft.UI.Colors.Transparent);
	// 65% black: Settings' and the search results' background when acrylic is off.
	private readonly SolidColorBrush _dimBackgroundBrush = new(Windows.UI.Color.FromArgb(0xA6, 0x00, 0x00, 0x00));

	private List<(string AppName, string AppTitle, InstallState? Status, bool IsRunning)> _installedMenuSnapshot = [];
	private string? _installedMenuActiveAppName;
	private List<string> _installedMenuSuspended = [];

	private readonly Dictionary<string, GameNavEntry> _gameNavEntries = new(StringComparer.Ordinal);
	private readonly Dictionary<string, Task<ImageSource?>> _executableIcons = new(StringComparer.OrdinalIgnoreCase);
	private InstallItem? _activeInstall;
	private string? _decoratedAppName;
	private DownloadSummary? _lastDownloadSummary;
	private TaskbarProgress? _taskbarProgress;

	private sealed class GameNavEntry
	{
		public required NavigationViewItem Item { get; init; }
		public required string Title { get; init; }
		public required Button PlayButton { get; init; }
		public required StackPanel StatusPanel { get; init; }
		public required FontIcon ActionIcon { get; init; }
		public required TextBlock ProgressText { get; init; }
		public required InfoBadge Badge { get; init; }
		public required LinearGradientBrush? ProgressBrush { get; init; }
		public required bool CanLaunch { get; init; }
	}

	private sealed class SearchSuggestion
	{
		public required string DisplayText { get; init; }
		public required string Subtitle { get; init; }
		public string? AppName { get; init; }
		public string? StoreQuery { get; init; }
		public StoreSearchResult? StoreResult { get; init; }
		public ImageSource? Thumbnail { get; init; }
		public string? IconGlyph { get; init; }
		public bool IsLoading { get; init; }
		public Visibility SkeletonVisibility { get; init; } = Visibility.Collapsed;
	}


	public MainWindow()
	{
		InitializeComponent();
		SetWindowIcon();

		_contentBackground = ContentFrame.Background;
		foreach (var key in PaneBackgroundKeys)
			NavControl.Resources[key] = _transparentChromeBrush;
		ContentFrame.Navigating += ContentFrame_Navigating;

		_taskbarProgress = TaskbarProgress.TryCreate(this);

		_settingsManager = App.GetService<SettingsManager>();
		_settingsManager.AcrylicEnabledChanged += OnAcrylicEnabledChanged;
		this.Closed += Window_Closed;
		ApplyWindowBackdrop();
		ApplyAcrylicBlurs();
		ExtendsContentIntoTitleBar = true;
		SetTitleBar(AppTitleBar);
		StyleCaptionButtons();
		GlobalSearchBox.Loaded += (_, _) =>
		{
			UpdateTitleBarInteractiveRegion();
			ApplySearchResultsBackground();
		};
		GlobalSearchBox.SizeChanged += (_, _) => UpdateTitleBarInteractiveRegion();

		_authManager = App.GetService<AuthManager>();
		_installManager = App.GetService<InstallManager>();
		_libraryManager = App.GetService<LibraryManager>();
		_storeCatalog = App.GetService<IStoreCatalog>();
		_log = App.GetService<ILogger>();

		_libraryManager.LibraryUpdated += LibraryUpdatedHandler;
		_libraryManager.GameStatusUpdated += GameStatusUpdatedHandler;
		CurrentDownload.SummaryChanged += OnDownloadSummaryChanged;
		_installManager.InstallationStatusChanged += InstallationStatusChangedHandler;
		_installManager.InstallProgressUpdate += InstallProgressUpdateHandler;
		NavControl.PaneOpening += (_, _) => UpdatePaneBlurWidth();
		NavControl.PaneClosing += (_, _) => UpdatePaneBlurWidth();
		NavControl.PaneOpened += (_, _) => UpdatePaneDownloadState();
		NavControl.PaneClosed += (_, _) => UpdatePaneDownloadState();
		NavControl.DisplayModeChanged += (_, _) =>
		{
			UpdatePaneBlurWidth();
			UpdatePaneDownloadState();
		};
		CurrentDownload.PublishCurrentSummary();
		_activeInstall = IsActionRunning(_installManager.CurrentInstall) ? _installManager.CurrentInstall : null;
		RebuildInstalledMenu();

		IsLoggedIn = false;
		_authManager.AuthStatusChanged += AuthStatusChangedHandler;
	}

	/// <summary>
	/// Square caption buttons that sit on the backdrop like Fluent Launcher's: the tall title bar
	/// makes them 48px high, and minimize/maximize only lighten on hover. Close keeps the system red.
	/// </summary>
	private void StyleCaptionButtons()
	{
		var titleBar = AppWindow.TitleBar;
		titleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
		titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
		titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
		titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF);
		titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
		titleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
		titleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
		titleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;
		titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF);
	}

	private void SetWindowIcon()
	{
		var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Crimson.ico");
		if (File.Exists(iconPath))
			AppWindow.SetIcon(iconPath);
		else
			_log.Warning("Window icon was not found at {IconPath}", iconPath);
	}

	private void NavControl_BackRequested(NavigationView sender,
	NavigationViewBackRequestedEventArgs args)
	{
		// Don't go back if the nav pane is overlayed.
		if (NavControl.IsPaneOpen &&
			NavControl.DisplayMode is NavigationViewDisplayMode.Compact or NavigationViewDisplayMode.Minimal)
			return;

		if (ContentFrame.Content is StoreWebPage storeWebPage && storeWebPage.TryGoBack())
			return;

		if (ContentFrame.CanGoBack)
			ContentFrame.GoBack();
	}

	private void UpdateTitleBarInteractiveRegion()
	{
		if (!ExtendsContentIntoTitleBar || GlobalSearchBox.XamlRoot is null)
			return;

		var inputSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
		if (GlobalSearchBox.Visibility != Visibility.Visible ||
			GlobalSearchBox.ActualWidth <= 0 ||
			GlobalSearchBox.ActualHeight <= 0)
		{
			inputSource.SetRegionRects(NonClientRegionKind.Passthrough, []);
			return;
		}

		var scale = GlobalSearchBox.XamlRoot.RasterizationScale;
		var bounds = GlobalSearchBox.TransformToVisual(null).TransformBounds(
			new Windows.Foundation.Rect(0, 0, GlobalSearchBox.ActualWidth, GlobalSearchBox.ActualHeight));
		inputSource.SetRegionRects(
			NonClientRegionKind.Passthrough,
			[new Windows.Graphics.RectInt32(
				(int)Math.Round(bounds.X * scale),
				(int)Math.Round(bounds.Y * scale),
				(int)Math.Round(bounds.Width * scale),
				(int)Math.Round(bounds.Height * scale))]);
	}

	/// <summary>
	/// The results popup is its own window (AutoSuggestBox resets ShouldConstrainToRootBounds on
	/// open, and changing it afterwards throws), and Windows draws no backdrop behind it. Its list
	/// stays transparent over SearchResultsBlur, which blurs the page in the main window. With
	/// acrylic off, the list brush the template draws with turns 65% black instead.
	/// </summary>
	private void ApplySearchResultsBackground()
	{
		if (GlobalSearchBox.Resources["AutoSuggestBoxSuggestionsListBackground"] is SolidColorBrush listBrush)
			listBrush.Color = _settingsManager.AcrylicEnabled ? Microsoft.UI.Colors.Transparent : _dimBackgroundBrush.Color;

		if (_searchResultsPopup is null &&
			FindDescendant<Popup>(GlobalSearchBox, "SuggestionsPopup") is { Child: FrameworkElement container } popup)
		{
			_searchResultsPopup = popup;
			popup.Opened += (_, _) => UpdateSearchResultsBlur();
			popup.Closed += (_, _) => UpdateSearchResultsBlur();
			container.SizeChanged += (_, _) => UpdateSearchResultsBlur();
		}

		UpdateSearchResultsBlur();
	}

	private Popup? _searchResultsPopup;

	private void UpdateSearchResultsBlur()
	{
		if (_searchResultsPopup is not { IsOpen: true, Child: FrameworkElement container } ||
			!_settingsManager.AcrylicEnabled ||
			container.ActualHeight <= 0 ||
			Content is not UIElement root)
		{
			SearchResultsBlur.Visibility = Visibility.Collapsed;
			return;
		}

		// The results open right under the search box, at its width.
		var origin = GlobalSearchBox.TransformToVisual(root)
			.TransformPoint(new Windows.Foundation.Point(0, GlobalSearchBox.ActualHeight));
		try
		{
			origin = container.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, 0));
		}
		catch (ArgumentException)
		{
		}
		SearchResultsBlur.Margin = new Thickness(origin.X, origin.Y, 0, 0);
		SearchResultsBlur.Width = container.ActualWidth;
		SearchResultsBlur.Height = container.ActualHeight;
		SearchResultsBlur.Visibility = Visibility.Visible;
	}

	private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
	{
		for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
		{
			var child = VisualTreeHelper.GetChild(root, i);
			if (child is T match && match.Name == name)
				return match;
			if (FindDescendant<T>(child, name) is { } found)
				return found;
		}

		return null;
	}

	private void GlobalSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
	{
		if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
			BeginSearch(sender.Text);
		ApplySearchResultsBackground();
	}

	private void BeginSearch(string value)
	{
		CancelStoreSearch();

		var query = NormalizeSearchQuery(value);
		var shouldSearchStore = query.Length >= 2;
		UpdateSearchSuggestions(query, showStoreLoading: shouldSearchStore);
		if (!shouldSearchStore)
			return;

		var cancellationSource = new CancellationTokenSource();
		_storeSearchCancellation = cancellationSource;
		_ = LoadStoreSearchSuggestionsAsync(query, cancellationSource);
	}

	private async Task LoadStoreSearchSuggestionsAsync(
		string query,
		CancellationTokenSource cancellationSource)
	{
		try
		{
			await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationSource.Token);
			var page = await _storeCatalog.SearchAsync(
				new StoreQuery { Keywords = query, Count = 5 },
				cancellationSource.Token);
			if (ReferenceEquals(_storeSearchCancellation, cancellationSource) &&
				string.Equals(NormalizeSearchQuery(GlobalSearchBox.Text), query, StringComparison.Ordinal))
				UpdateSearchSuggestions(query, page.Results);
		}
		catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
		{
		}
		catch (StoreCatalogException)
		{
			// Library matches and the full-search entry still work without store suggestions.
			if (ReferenceEquals(_storeSearchCancellation, cancellationSource))
				UpdateSearchSuggestions(query, []);
		}
		finally
		{
			if (ReferenceEquals(_storeSearchCancellation, cancellationSource))
				_storeSearchCancellation = null;
			cancellationSource.Dispose();
		}
	}

	private void GlobalSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
	{
		if (args.ChosenSuggestion is SearchSuggestion { IsLoading: true })
			return;

		var query = NormalizeSearchQuery(args.QueryText);
		if (query.Length == 0)
			return;

		var suggestion = args.ChosenSuggestion as SearchSuggestion
						 ?? GetLocalSearchSuggestions(query).FirstOrDefault()
						 ?? CreateStoreSearchSuggestion(query);

		ClearGlobalSearch();
		if (suggestion.AppName is not null)
		{
			NavigateToGame(suggestion.AppName, new EntranceNavigationTransitionInfo());
			return;
		}

		if (suggestion.StoreResult is not null)
			NavigateToStoreProduct(suggestion.StoreResult.ToProductRequest());
		else
			NavigateToStoreSearch(suggestion.StoreQuery!);
	}

	private void UpdateSearchSuggestions(
		string query,
		IReadOnlyList<StoreSearchResult>? storeResults = null,
		bool showStoreLoading = false)
	{
		if (query.Length == 0)
		{
			GlobalSearchBox.ItemsSource = null;
			return;
		}

		var suggestions = GetLocalSearchSuggestions(query);
		if (storeResults is not null)
		{
			suggestions.AddRange(storeResults.Select(result => new SearchSuggestion
			{
				DisplayText = result.Title,
				Subtitle = "Epic Games Store",
				StoreResult = result,
				Thumbnail = CreateSearchThumbnail(result.ImageUrl)
			}));
		}
		else if (showStoreLoading)
		{
			for (var i = 0; i < 3; i++)
				suggestions.Add(CreateStoreLoadingSuggestion());
		}

		suggestions.Add(CreateStoreSearchSuggestion(query));
		GlobalSearchBox.ItemsSource = suggestions;
	}

	private List<SearchSuggestion> GetLocalSearchSuggestions(string query)
	{
		return _libraryCache
			.Where(game => game.Metadata is not null && !game.IsDlc())
			.Where(game =>
				(game.AppTitle?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
				game.AppName.Contains(query, StringComparison.OrdinalIgnoreCase))
			.OrderBy(game => game.AppTitle?.StartsWith(query, StringComparison.CurrentCultureIgnoreCase) == true ? 0 : 1)
			.ThenBy(game => game.AppTitle, StringComparer.CurrentCultureIgnoreCase)
			.Take(5)
			.Select(game => new SearchSuggestion
			{
				DisplayText = string.IsNullOrWhiteSpace(game.AppTitle) ? game.AppName : game.AppTitle,
				Subtitle = "In your library",
				AppName = game.AppName,
				Thumbnail = CreateSearchThumbnail(game)
			})
			.ToList();
	}

	private static ImageSource? CreateSearchThumbnail(Game game)
	{
		var url = game.Metadata?.KeyImages?
			.FirstOrDefault(image => image.Type == "DieselGameBoxTall")?.Url
				  ?? game.Metadata?.KeyImages?
					  .FirstOrDefault(image => image.Type == "DieselGameBox")?.Url;
		return CreateSearchThumbnail(url);
	}

	private static ImageSource? CreateSearchThumbnail(string? url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
			!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
			return null;

		return new BitmapImage
		{
			DecodePixelType = DecodePixelType.Logical,
			DecodePixelHeight = 84,
			UriSource = uri
		};
	}

	private static SearchSuggestion CreateStoreLoadingSuggestion() => new()
	{
		DisplayText = string.Empty,
		Subtitle = string.Empty,
		IsLoading = true,
		SkeletonVisibility = Visibility.Visible
	};

	private static SearchSuggestion CreateStoreSearchSuggestion(string query) => new()
	{
		DisplayText = $"Search Epic Games Store for “{query}”",
		Subtitle = "View all Store results",
		StoreQuery = query,
		IconGlyph = "\uE719"
	};

	private void NavigateToStoreSearch(string query)
	{
		NavControl.SelectedItem = StoreNavItem;
		if (ContentFrame.Content is StorePage storePage)
		{
			storePage.ShowSearch(query);
			return;
		}

		_currentGameAppName = null;
		ContentFrame.Navigate(typeof(StorePage), query, new EntranceNavigationTransitionInfo());
	}

	private void NavigateToStoreProduct(StoreProductRequest request)
	{
		NavControl.SelectedItem = StoreNavItem;
		_currentGameAppName = null;
		ContentFrame.Navigate(typeof(StoreProductPage), request, new EntranceNavigationTransitionInfo());
	}

	private void ClearGlobalSearch()
	{
		CancelStoreSearch();
		GlobalSearchBox.Text = string.Empty;
		GlobalSearchBox.ItemsSource = null;
	}

	private static string NormalizeSearchQuery(string value)
	{
		var query = value.Trim();
		return query.Length > 100 ? query[..100] : query;
	}

	private void CancelStoreSearch()
	{
		var cancellationSource = _storeSearchCancellation;
		_storeSearchCancellation = null;
		cancellationSource?.Cancel();
	}

	private void NavControl_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
	{
		if (args.IsSettingsInvoked == true)
		{
			NavControl_Navigate(typeof(SettingsPage), args.RecommendedNavigationTransitionInfo);
			return;
		}

		if (args.InvokedItemContainer?.Tag is not string tag || tag.Length == 0)
			return;

		if (tag.StartsWith(GameNavTagPrefix, StringComparison.Ordinal))
		{
			NavigateToGame(tag[GameNavTagPrefix.Length..], args.RecommendedNavigationTransitionInfo);
			return;
		}

		var pageType = Type.GetType(tag);
		if (pageType is not null)
			NavControl_Navigate(pageType, args.RecommendedNavigationTransitionInfo);
	}

	private void NavigateToGame(string appName, NavigationTransitionInfo transitionInfo)
	{
		if (Equals(ContentFrame.CurrentSourcePageType, typeof(GameInfoPage)) &&
			string.Equals(_currentGameAppName, appName, StringComparison.Ordinal))
			return;

		_currentGameAppName = appName;
		ContentFrame.Navigate(typeof(GameInfoPage), appName, transitionInfo);
	}

	private void ContentFrame_Navigating(object sender, NavigatingCancelEventArgs e)
	{
		if (e.SourcePageType == typeof(GameInfoPage) && e.Parameter is string appName)
			ShowGameBackground(GameArtwork.SelectBackgroundUrl(_libraryManager.GetGameInfo(appName)));
		else
			HideGameBackground();

		if (e.SourcePageType == typeof(SettingsPage))
			ContentFrame.Background = SettingsBackground;
	}

	private void ShowGameBackground(string? imageUrl)
	{
		GameBackgroundBrush.ImageSource = CreateGameBackgroundImage(imageUrl);
		GameBackgroundLayer.Visibility = Visibility.Visible;
		UpdatePaneBlurWidth();

		// Let the artwork run behind the page and the title bar above it.
		ContentFrame.Background = _transparentChromeBrush;
	}

	/// <summary>
	/// Sizes the blur behind the docked pane (which is see-through) to the pane's width. An
	/// overlay pane (compact/minimal) brings its own acrylic, so only its closed strip is blurred.
	/// </summary>
	private void UpdatePaneBlurWidth()
	{
		var inset = NavControl.DisplayMode switch
		{
			NavigationViewDisplayMode.Expanded => NavControl.IsPaneOpen
				? NavControl.OpenPaneLength
				: NavControl.CompactPaneLength,
			NavigationViewDisplayMode.Compact => NavControl.CompactPaneLength,
			_ => 0
		};
		PaneBlur.Width = inset;
	}

	/// <summary>
	/// The pane and search pill blurs follow the acrylic setting; with it off they stay see-through.
	/// </summary>
	private Brush? SettingsBackground => _settingsManager.AcrylicEnabled ? _contentBackground : _dimBackgroundBrush;

	private void ApplyAcrylicBlurs()
	{
		var visibility = _settingsManager.AcrylicEnabled ? Visibility.Visible : Visibility.Collapsed;
		PaneBlur.Visibility = visibility;
		SearchBlur.Visibility = visibility;
	}

	private void HideGameBackground()
	{
		GameBackgroundBrush.ImageSource = null;
		GameBackgroundLayer.Visibility = Visibility.Collapsed;

		ContentFrame.Background = _contentBackground;
	}

	private static ImageSource? CreateGameBackgroundImage(string? imageUrl)
	{
		if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) ||
			!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
			return null;

		return new BitmapImage
		{
			DecodePixelType = DecodePixelType.Logical,
			DecodePixelWidth = 2560,
			UriSource = uri
		};
	}

	private void LibraryUpdatedHandler(IEnumerable<Game> games)
	{
		if (games == null) return;
		var snapshot = games.ToList();
		DispatcherQueue.TryEnqueue(() =>
		{
			_libraryCache = snapshot;
			RebuildInstalledMenu();
			if (!string.IsNullOrWhiteSpace(GlobalSearchBox.Text))
				BeginSearch(GlobalSearchBox.Text);
		});
	}

	private void GameStatusUpdatedHandler(Game game)
	{
		if (game == null) return;
		DispatcherQueue.TryEnqueue(() =>
		{
			var index = _libraryCache.FindIndex(cached =>
				string.Equals(cached.AppName, game.AppName, StringComparison.Ordinal));
			if (index >= 0)
				_libraryCache[index] = game;
			else
				_libraryCache.Add(game);

			RebuildInstalledMenu();
		});
	}

	private void RebuildInstalledMenu(bool force = false)
	{
		var activeAppName = _activeInstall?.AppName;
		// Installs pushed off the pipeline by another download are not "installed" yet, so their
		// local state never marks them paused; list them explicitly so they stay in the pane.
		var suspended = _installManager.SuspendedInstalls
			.Where(item => !string.Equals(item.AppName, activeAppName, StringComparison.Ordinal))
			.GroupBy(item => item.AppName, StringComparer.Ordinal)
			.ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

		var games = new List<Game>(_libraryCache);
		foreach (var appName in suspended.Keys.Prepend(activeAppName))
		{
			if (appName is null ||
				games.Any(game => string.Equals(game.AppName, appName, StringComparison.Ordinal)))
				continue;
			var game = _libraryManager.GetGameInfo(appName);
			if (game?.Metadata is not null && !game.IsDlc())
				games.Add(game);
		}

		var installed = games
			.Where(game => game.Metadata is not null && !game.IsDlc())
			.Where(game => IsPaneListed(game, activeAppName) || suspended.ContainsKey(game.AppName))
			.OrderBy(game => game.AppTitle, StringComparer.CurrentCultureIgnoreCase)
			.ToList();

		var snapshot = installed
			.Select(game => (game.AppName, game.AppTitle, game.LocalAppState?.InstallStatus,
				_libraryManager.IsAppRunning(game.AppName)))
			.ToList();
		var suspendedNames = suspended.Keys.Order(StringComparer.Ordinal).ToList();
		if (!force && snapshot.SequenceEqual(_installedMenuSnapshot) &&
			suspendedNames.SequenceEqual(_installedMenuSuspended) &&
			string.Equals(activeAppName, _installedMenuActiveAppName, StringComparison.Ordinal))
			return;
		_installedMenuSnapshot = snapshot;
		_installedMenuSuspended = suspendedNames;
		_installedMenuActiveAppName = activeAppName;
		_gameNavEntries.Clear();
		// Rebuilt entries start undecorated; resetting the old one would wipe a suspended visual.
		_decoratedAppName = null;

		for (var i = NavControl.MenuItems.Count - 1; i >= 0; i--)
		{
			if (NavControl.MenuItems[i] is NavigationViewItem { Tag: string tag } &&
				tag.StartsWith(GameNavTagPrefix, StringComparison.Ordinal))
				NavControl.MenuItems.RemoveAt(i);
		}

		foreach (var game in installed)
			NavControl.MenuItems.Add(CreateInstalledGameItem(game, suspended.ContainsKey(game.AppName)));

		foreach (var item in suspended.Values)
			ApplySuspendedVisual(item);

		ApplyInstallVisual(_activeInstall);
	}

	private void ApplySuspendedVisual(InstallItem installItem)
	{
		if (!_gameNavEntries.TryGetValue(installItem.AppName, out var target))
			return;

		var percent = Math.Clamp(installItem.ProgressPercentage, 0, 100);
		var label = CurrentDownloadControl.GetActionLabel(installItem.Action);

		target.PlayButton.Visibility = Visibility.Collapsed;
		target.StatusPanel.Visibility = Visibility.Visible;
		target.ActionIcon.Glyph = GetActionGlyph(installItem.Action, ActionStatus.Paused);
		target.ProgressText.Text = $"{percent}%";
		SetProgressFill(target.ProgressBrush, percent);
		ToolTipService.SetToolTip(target.Item, $"{target.Title} - {label} paused at {percent}%");
	}

	private static bool IsPaneListed(Game game, string? activeAppName) =>
		string.Equals(game.AppName, activeAppName, StringComparison.Ordinal) ||
		game.LocalAppState?.InstallStatus is InstallState.Installed or InstallState.NeedUpdate
			or InstallState.Installing or InstallState.InstallationPaused
			or InstallState.Updating or InstallState.UpdatingPaused
			or InstallState.Repairing;

	private NavigationViewItem CreateInstalledGameItem(Game game, bool isSuspended = false)
	{
		var appName = game.AppName;
		var title = string.IsNullOrWhiteSpace(game.AppTitle) ? appName : game.AppTitle;
		var canLaunch = !isSuspended &&
			game.LocalAppState?.InstallStatus is InstallState.Installed or InstallState.NeedUpdate;
		var isActive = isSuspended || string.Equals(appName, _activeInstall?.AppName, StringComparison.Ordinal);
		var isRunning = canLaunch && _libraryManager.IsAppRunning(appName);

		var titleBlock = new TextBlock
		{
			Text = title,
			VerticalAlignment = VerticalAlignment.Center,
			TextTrimming = TextTrimming.CharacterEllipsis,
			TextWrapping = TextWrapping.NoWrap
		};
		Grid.SetColumn(titleBlock, 0);

		var actionIcon = new FontIcon { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
		var progressText = new TextBlock
		{
			Margin = new Thickness(4, 0, 0, 0),
			VerticalAlignment = VerticalAlignment.Center,
			FontSize = 12
		};
		var statusPanel = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			VerticalAlignment = VerticalAlignment.Center,
			Visibility = Visibility.Collapsed
		};
		statusPanel.Children.Add(actionIcon);
		statusPanel.Children.Add(progressText);
		Grid.SetColumn(statusPanel, 1);

		var playButton = new Button
		{
			Content = new FontIcon { Glyph = isRunning ? "\uE71A" : "\uE768", FontSize = 16 },
			Width = 40,
			Height = 40,
			Padding = new Thickness(0),
			CornerRadius = new CornerRadius(6),
			Margin = new Thickness(4, 0, 0, 0),
			VerticalAlignment = VerticalAlignment.Center,
			Visibility = canLaunch ? Visibility.Visible : Visibility.Collapsed,
			Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
			BorderThickness = new Thickness(0)
		};
		ToolTipService.SetToolTip(playButton, isRunning ? $"Stop {title}" : $"Play {title}");
		playButton.Click += (_, _) =>
		{
			if (_libraryManager.IsAppRunning(appName))
				StopGame(appName, title);
			else
				LaunchGame(appName, title);
		};
		playButton.Tapped += (_, e) => e.Handled = true;
		Grid.SetColumn(playButton, 2);

		var layout = new Grid();
		layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		layout.Children.Add(titleBlock);
		layout.Children.Add(statusPanel);
		layout.Children.Add(playButton);

		var badge = new InfoBadge { Visibility = Visibility.Collapsed };
		var progressBrush = isActive ? CreateProgressBrush() : null;

		var item = new NavigationViewItem
		{
			Content = layout,
			Tag = GameNavTagPrefix + appName,
			Icon = CreateGameIcon(game),
			InfoBadge = badge
		};
		item.Resources["NavigationViewItemOnLeftIconBoxHeight"] = (double)GameIconBoxHeight;
		if (progressBrush is not null)
		{
			item.Background = progressBrush;
			foreach (var key in ProgressBackgroundKeys)
				item.Resources[key] = progressBrush;
		}

		ToolTipService.SetToolTip(item, title);

		_gameNavEntries[appName] = new GameNavEntry
		{
			Item = item,
			Title = title,
			PlayButton = playButton,
			StatusPanel = statusPanel,
			ActionIcon = actionIcon,
			ProgressText = progressText,
			Badge = badge,
			ProgressBrush = progressBrush,
			CanLaunch = canLaunch
		};
		return item;
	}

	private void InstallationStatusChangedHandler(InstallItem? installItem)
	{
		DispatcherQueue.TryEnqueue(() =>
		{
			// Status events can come from games that were pushed into the paused list; the active
			// visuals always mirror the install that currently owns the pipeline. A queued install
			// is mirrored too, otherwise newly-started downloads stay out of the pane until the
			// pipeline finishes preparing their manifest.
			var current = _installManager.CurrentInstall
				?? (installItem?.Status is ActionStatus.Pending
					or ActionStatus.Processing
					or ActionStatus.Cancelling
					? installItem
					: null);
			_activeInstall = IsActionRunning(current) ? current : null;
			RebuildInstalledMenu();
			ApplyInstallVisual(_activeInstall);
		});
	}

	private void InstallProgressUpdateHandler(InstallItem? installItem)
	{
		if (installItem is null) return;
		DispatcherQueue.TryEnqueue(() => ApplyInstallVisual(installItem));
	}

	private static bool IsActionRunning(InstallItem? installItem) =>
		installItem?.Status is ActionStatus.Pending or ActionStatus.OnGoing or ActionStatus.Processing
			or ActionStatus.Paused or ActionStatus.Cancelling;

	private void ApplyInstallVisual(InstallItem? installItem)
	{
		UpdateTaskbarProgress(installItem);
		var activeAppName = IsActionRunning(installItem) ? installItem!.AppName : null;

		if (!string.Equals(_decoratedAppName, activeAppName, StringComparison.Ordinal))
		{
			if (_decoratedAppName is not null &&
				_gameNavEntries.TryGetValue(_decoratedAppName, out var previous))
				ResetEntry(previous);
			_decoratedAppName = activeAppName;
		}

		if (activeAppName is null || !_gameNavEntries.TryGetValue(activeAppName, out var target))
			return;

		var percent = Math.Clamp(installItem!.ProgressPercentage, 0, 100);
		var label = CurrentDownloadControl.GetActionLabel(installItem.Action);

		target.PlayButton.Visibility = Visibility.Collapsed;
		target.StatusPanel.Visibility = Visibility.Visible;
		target.ActionIcon.Glyph = GetActionGlyph(installItem.Action, installItem.Status);

		if (installItem.IsVerifying)
		{
			// Progress is unknown until the disk check finishes, so no percentage is shown.
			target.ProgressText.Text = "Checking";
			target.Badge.Value = -1;
			UpdateBadgeVisibility();
			SetProgressFill(target.ProgressBrush, 0);
			ToolTipService.SetToolTip(target.Item, $"{target.Title} - checking files");
			return;
		}

		target.ProgressText.Text = $"{percent}%";

		target.Badge.Value = percent;
		UpdateBadgeVisibility();

		SetProgressFill(target.ProgressBrush, percent);
		ToolTipService.SetToolTip(target.Item,
			installItem.Status == ActionStatus.Paused
				? $"{target.Title} - {label} paused at {percent}%"
				: $"{target.Title} - {label} {percent}%");
	}

	private void UpdateTaskbarProgress(InstallItem? installItem)
	{
		if (_taskbarProgress is null)
			return;

		if (installItem is null || !IsActionRunning(installItem))
		{
			_taskbarProgress.Clear();
			return;
		}

		if (installItem.IsVerifying)
		{
			_taskbarProgress.SetIndeterminate();
			return;
		}

		var percent = Math.Clamp(installItem.ProgressPercentage, 0, 100);
		switch (installItem.Status)
		{
			case ActionStatus.Paused:
				_taskbarProgress.SetPaused(percent);
				break;
			case ActionStatus.Pending:
			case ActionStatus.Cancelling:
				_taskbarProgress.SetIndeterminate();
				break;
			default:
				_taskbarProgress.SetNormal(percent);
				break;
		}
	}

	private static void ResetEntry(GameNavEntry entry)
	{
		entry.StatusPanel.Visibility = Visibility.Collapsed;
		entry.PlayButton.Visibility = entry.CanLaunch ? Visibility.Visible : Visibility.Collapsed;
		entry.Badge.Visibility = Visibility.Collapsed;
		SetProgressFill(entry.ProgressBrush, 0);
		ToolTipService.SetToolTip(entry.Item, entry.Title);
	}

	private static string GetActionGlyph(ActionType action, ActionStatus status) => status switch
	{
		ActionStatus.Paused => "\uE769",
		ActionStatus.Cancelling => "\uE711",
		_ => action switch
		{
			ActionType.Install => "\uE896",
			ActionType.Update => "\uE895",
			ActionType.Repair or ActionType.Verify => "\uE90F",
			ActionType.Uninstall => "\uE74D",
			ActionType.Move => "\uE8DE",
			ActionType.Import => "\uE8B5",
			_ => "\uE896"
		}
	};

	private static LinearGradientBrush CreateProgressBrush()
	{
		var fill = GetSelectionFillColor();
		var lead = Windows.UI.Color.FromArgb((byte)Math.Min(255, fill.A * 2), fill.R, fill.G, fill.B);

		var brush = new LinearGradientBrush
		{
			StartPoint = new Windows.Foundation.Point(0, 0.5),
			EndPoint = new Windows.Foundation.Point(1, 0.5)
		};
		brush.GradientStops.Add(new GradientStop { Offset = 0, Color = lead });
		brush.GradientStops.Add(new GradientStop { Offset = 0, Color = fill });
		brush.GradientStops.Add(new GradientStop { Offset = 0, Color = Microsoft.UI.Colors.Transparent });
		brush.GradientStops.Add(new GradientStop { Offset = 1, Color = Microsoft.UI.Colors.Transparent });
		return brush;
	}

	private static Windows.UI.Color GetSelectionFillColor() =>
		Application.Current.Resources.TryGetValue("SubtleFillColorSecondaryBrush", out var value) &&
		value is SolidColorBrush brush
			? brush.Color
			: Windows.UI.Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF);

	private static void SetProgressFill(LinearGradientBrush? brush, int percent)
	{
		if (brush is null) return;
		var offset = Math.Clamp(percent / 100d, 0d, 1d);
		brush.GradientStops[1].Offset = offset;
		brush.GradientStops[2].Offset = offset;
	}

	private IconElement? CreateGameIcon(Game game)
	{
		var executable = GetExecutablePath(game);
		if (executable is null)
			return CreateBoxArtIcon(game);

		// The pane is rebuilt often, so the icon is filled in once the executable's icon is read.
		var icon = new ImageIcon { Width = GameIconBoxHeight, Height = GameIconBoxHeight };
		_ = LoadExecutableIconAsync(icon, executable, game);
		return icon;
	}

	private static string? GetExecutablePath(Game game)
	{
		var state = game.LocalAppState;
		if (state?.InstallStatus is not (InstallState.Installed or InstallState.NeedUpdate) ||
			string.IsNullOrWhiteSpace(state.InstallPath) || string.IsNullOrWhiteSpace(state.Executable))
			return null;

		try
		{
			return InstallPathPolicy.ResolveFile(state.InstallPath, state.Executable);
		}
		catch (Exception)
		{
			return null;
		}
	}

	private async Task LoadExecutableIconAsync(ImageIcon icon, string executable, Game game)
	{
		if (!_executableIcons.TryGetValue(executable, out var load))
			_executableIcons[executable] = load = ReadExecutableIconAsync(executable);

		var source = await load;
		if (source is null)
		{
			// Not cached, so an icon that becomes readable later (e.g. after a repair) is picked up.
			_executableIcons.Remove(executable);
			source = CreateBoxArtSource(game);
		}
		icon.Source = source;
	}

	private async Task<ImageSource?> ReadExecutableIconAsync(string executable)
	{
		try
		{
			var file = await StorageFile.GetFileFromPathAsync(executable);
			using var thumbnail = await file.GetThumbnailAsync(
				ThumbnailMode.SingleItem, GameIconBoxHeight * 2, ThumbnailOptions.ResizeThumbnail);
			if (thumbnail is null || thumbnail.Size == 0)
				return null;

			var bitmap = new BitmapImage();
			await bitmap.SetSourceAsync(thumbnail);
			return bitmap;
		}
		catch (Exception ex)
		{
			_log.Warning(ex, "MainWindow: Failed to read the icon of {Executable}", executable);
			return null;
		}
	}

	private static IconElement? CreateBoxArtIcon(Game game)
	{
		var source = CreateBoxArtSource(game);
		return source is null
			? null
			: new ImageIcon { Source = source, Width = GameIconBoxHeight, Height = GameIconBoxHeight };
	}

	private static ImageSource? CreateBoxArtSource(Game game)
	{
		var keyImages = game.Metadata?.KeyImages;
		if (keyImages is null) return null;

		var url = keyImages.FirstOrDefault(image => image.Type == "DieselGameBoxTall")?.Url
				  ?? keyImages.FirstOrDefault(image => image.Type == "DieselGameBox")?.Url;

		if (string.IsNullOrEmpty(url)) return null;

		var bitmap = new BitmapImage
		{
			DecodePixelType = DecodePixelType.Logical,
			DecodePixelHeight = GameIconBoxHeight * 2
		};
		bitmap.UriSource = new Uri(url);
		return bitmap;
	}

	private void LaunchGame(string appName, string title)
	{
		_log.Information("MainWindow: Launching {Title} ({AppName}) from the navigation pane", title, appName);
		_ = Task.Run(() => _libraryManager.LaunchApp(appName));
	}

	private void StopGame(string appName, string title)
	{
		_log.Information("MainWindow: Stopping {Title} ({AppName}) from the navigation pane", title, appName);
		_ = Task.Run(() => _libraryManager.StopApp(appName));
	}

	private void OnDownloadSummaryChanged(DownloadSummary summary)
	{
		_lastDownloadSummary = summary;
		ToolTipService.SetToolTip(DownloadsNavItem, summary.ToolTip);
		DownloadInfoBadge.Value = summary.IsVerifying ? -1 : summary.ProgressPercentage;
		UpdatePaneDownloadState();
	}

	private void UpdatePaneDownloadState()
	{
		UpdateDownloadsNavItems();
		UpdateBadgeVisibility();
	}

	/// <summary>
	/// While a download is active and the pane is open, the progress item already names the game,
	/// so it replaces the Downloads item: it opens the Downloads page and carries the selection.
	/// A collapsed pane hides the progress item's content, so the Downloads icon comes back there.
	/// </summary>
	private void UpdateDownloadsNavItems()
	{
		var showProgress = NavControl.IsPaneOpen && _lastDownloadSummary is { HasActiveDownload: true };
		var downloadsSelected = ReferenceEquals(NavControl.SelectedItem, DownloadsNavItem) ||
			ReferenceEquals(NavControl.SelectedItem, CurrentDownloadNavItem);

		DownloadsNavItem.Visibility = showProgress ? Visibility.Collapsed : Visibility.Visible;
		// Finished, failed or cancelled actions are listed on the Downloads page; the pane only
		// shows the progress item while something is actually downloading.
		CurrentDownloadNavItem.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
		CurrentDownloadNavItem.Tag = showProgress ? DownloadsNavItem.Tag : null;
		CurrentDownloadNavItem.SelectsOnInvoked = showProgress;
		CurrentDownloadNavItem.IsTabStop = showProgress;

		if (downloadsSelected)
			NavControl.SelectedItem = showProgress ? CurrentDownloadNavItem : DownloadsNavItem;
	}

	private void UpdateBadgeVisibility()
	{
		var collapsed = !NavControl.IsPaneOpen;

		DownloadInfoBadge.Visibility = collapsed && _lastDownloadSummary is { HasActiveDownload: true }
			? Visibility.Visible
			: Visibility.Collapsed;

		if (_decoratedAppName is not null && _gameNavEntries.TryGetValue(_decoratedAppName, out var entry))
			entry.Badge.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
	}

	private void NavControl_Navigate(
		Type navPageType,
		NavigationTransitionInfo transitionInfo)
	{
		// Get the page type before navigation so you can prevent duplicate
		// entries in the backstack.
		var preNavPageType = ContentFrame.CurrentSourcePageType;

		// Only navigate if the selected page isn't currently loaded.
		if (navPageType is not null && !Equals(preNavPageType, navPageType))
			ContentFrame.Navigate(navPageType, null, transitionInfo);
	}
	private void UpdateUIBasedOnAuthenticationStatus(AuthenticationStatus authStatus)
	{
		Log.Information("Auth status: {AuthStatus}", authStatus);

		// Sign-in covers the whole window, so the title bar search only appears once logged in.
		GlobalSearchBox.Visibility = authStatus == AuthenticationStatus.LoggedIn
			? Visibility.Visible
			: Visibility.Collapsed;
		UpdateTitleBarInteractiveRegion();

		switch (authStatus)
		{
			case AuthenticationStatus.Checking:
				NavControl.Visibility = Visibility.Collapsed;
				LoginPage.Visibility = Visibility.Collapsed;
				LoginModal.Visibility = Visibility.Visible;
				LoginModalTitle.Text = "Checking authentication status";
				LoginModalDescription.Text = "Please wait...";
				break;

			case AuthenticationStatus.LoggedOut:
				_libraryCache.Clear();
				RebuildInstalledMenu(force: true);
				ClearGlobalSearch();
				NavControl.Visibility = Visibility.Collapsed;
				LoginPage.Visibility = Visibility.Visible;
				LoginPage.InitWebView();
				break;

			case AuthenticationStatus.LoggedIn:
				Log.Information("Logged in");
				LoginModalTitle.Text = "Login Success";

				// Keep the authenticated WebView alive so the Store reuses its session,
				// and mirror the auth cookies to the store domain for future launches.
				_ = LoginPage.ShareCookiesWithStoreAsync();

				NavControl.Visibility = Visibility.Visible;
				NavControl.IsEnabled = true;
				LoginPage.Visibility = Visibility.Collapsed;
				LoginModal.Visibility = Visibility.Collapsed;
				NavControl.SelectedItem = NavControl.MenuItems[0];
				NavControl_Navigate(typeof(LibraryPage), new EntranceNavigationTransitionInfo());
				Log.Information("Opening Library Page");

				_ = RestorePendingInstallAsync();
				break;

			case AuthenticationStatus.LoginFailed:
				LoginModalTitle.Text = "Login failed";
				LoginModalDescription.Text = "Please try again";
				LoginModal.Visibility = Visibility.Visible;
				NavControl.Visibility = Visibility.Collapsed;
				LoginPage.Visibility = Visibility.Visible;
				break;
		}
	}

	private void AuthStatusChangedHandler(object sender, AuthStatusChangedEventArgs e)
	{
		DispatcherQueue.TryEnqueue(() => UpdateUIBasedOnAuthenticationStatus(e.NewStatus));
	}

	/// <summary>
	/// Loads the library first so game titles and install locations are available, then
	/// restores the saved download. The restore compares against the manifest and hashes
	/// every file, so it must not compete with the library refresh.
	/// </summary>
	private async Task RestorePendingInstallAsync()
	{
		try
		{
			await _libraryManager.GetLibraryData();
		}
		catch (Exception ex)
		{
			_log.Error(ex, "MainWindow: could not load the installed games list");
		}

		try
		{
			await _installManager.LoadPendingInstalls();
		}
		catch (Exception ex)
		{
			_log.Error(ex, "MainWindow: could not restore the pending download");
		}

		CurrentDownload.PublishCurrentSummary();
	}
	private void OnAcrylicEnabledChanged(bool enabled)
	{
		ApplyWindowBackdrop();
		ApplyAcrylicBlurs();
		ApplySearchResultsBackground();
		if (ContentFrame.Content is SettingsPage)
			ContentFrame.Background = SettingsBackground;
	}

	/// <summary>
	/// The window blurs the desktop behind it with acrylic. With the setting off it has no backdrop
	/// and the transparent surfaces stay as they are.
	/// </summary>
	private void ApplyWindowBackdrop()
	{
		if (!_settingsManager.AcrylicEnabled)
			SystemBackdrop = null;
		else if (SystemBackdrop is not DesktopAcrylicBackdrop)
			SystemBackdrop = new DesktopAcrylicBackdrop();
	}

	private void Window_Closed(object sender, WindowEventArgs args)
	{
		// This handler runs before App.OnExit, which cancels the close and only hides the window
		// while the tray icon is active. Tearing down here would leave the reopened window with
		// no backdrop and deaf to install/library events, freezing the pane's progress.
		if (App.HandleClosedEvents)
			return;

		_settingsManager.AcrylicEnabledChanged -= OnAcrylicEnabledChanged;

		_libraryManager.LibraryUpdated -= LibraryUpdatedHandler;
		_libraryManager.GameStatusUpdated -= GameStatusUpdatedHandler;
		CancelStoreSearch();
		CurrentDownload.SummaryChanged -= OnDownloadSummaryChanged;
		_installManager.InstallationStatusChanged -= InstallationStatusChangedHandler;
		_installManager.InstallProgressUpdate -= InstallProgressUpdateHandler;
		_taskbarProgress?.Clear();
	}
}
