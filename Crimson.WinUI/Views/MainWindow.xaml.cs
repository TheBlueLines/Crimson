using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Core;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;
using WinRT;

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

	private const string GameNavTagPrefix = "game:";

	public bool IsLoggedIn;
	private ILogger _log = App.GetService<ILogger>();
	private readonly AuthManager _authManager;
	private readonly InstallManager _installManager;
	private readonly LibraryManager _libraryManager;
	private readonly IStoreRepository _storeRepository;

	private List<Game> _libraryCache = new();
	private CancellationTokenSource? _storeSearchCancellation;
	private string? _currentGameAppName;

	private List<(string AppName, string AppTitle, InstallState? Status)> _installedMenuSnapshot = [];
	private string? _installedMenuActiveAppName;

	private readonly Dictionary<string, GameNavEntry> _gameNavEntries = new(StringComparer.Ordinal);
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
		public string? StoreProductSlug { get; init; }
		public ImageSource? Thumbnail { get; init; }
		public string? IconGlyph { get; init; }
		public bool IsLoading { get; init; }
		public Visibility SkeletonVisibility { get; init; } = Visibility.Collapsed;
	}

	WindowsSystemDispatcherQueueHelper _mWsdqHelper;
	MicaController _mBackdropController;
	SystemBackdropConfiguration _mConfigurationSource;

	public MainWindow()
	{
		InitializeComponent();
		SetWindowIcon();
		_taskbarProgress = TaskbarProgress.TryCreate(this);

		// Disable setting mica as default
		// We will config later when we do configuration manager
		TrySetSystemBackdrop();
		ExtendsContentIntoTitleBar = true;
		SetTitleBar(AppTitleBar);
		GlobalSearchBox.Loaded += (_, _) => UpdateTitleBarInteractiveRegion();
		GlobalSearchBox.SizeChanged += (_, _) => UpdateTitleBarInteractiveRegion();

		_authManager = App.GetService<AuthManager>();
		_installManager = App.GetService<InstallManager>();
		_libraryManager = App.GetService<LibraryManager>();
		_storeRepository = App.GetService<IStoreRepository>();
		_log = App.GetService<ILogger>();

		_libraryManager.LibraryUpdated += LibraryUpdatedHandler;
		_libraryManager.GameStatusUpdated += GameStatusUpdatedHandler;
		CurrentDownload.SummaryChanged += OnDownloadSummaryChanged;
		_installManager.InstallationStatusChanged += InstallationStatusChangedHandler;
		_installManager.InstallProgressUpdate += InstallProgressUpdateHandler;
		NavControl.PaneOpened += (_, _) => UpdateBadgeVisibility();
		NavControl.PaneClosed += (_, _) => UpdateBadgeVisibility();
		NavControl.DisplayModeChanged += (_, _) => UpdateBadgeVisibility();
		CurrentDownload.PublishCurrentSummary();
		_activeInstall = IsActionRunning(_installManager.CurrentInstall) ? _installManager.CurrentInstall : null;
		RebuildInstalledMenu();

		IsLoggedIn = false;
		_authManager.AuthStatusChanged += AuthStatusChangedHandler;
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

		if (ContentFrame.Content is StorePage storePage && storePage.TryGoBack())
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

	private void GlobalSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
	{
		if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
			BeginSearch(sender.Text);
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
			var results = await _storeRepository.SearchStore(query, cancellationSource.Token);
			if (ReferenceEquals(_storeSearchCancellation, cancellationSource) &&
				string.Equals(NormalizeSearchQuery(GlobalSearchBox.Text), query, StringComparison.Ordinal))
				UpdateSearchSuggestions(query, results);
		}
		catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
		{
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

		if (suggestion.StoreProductSlug is not null)
			NavigateToStore(StorePage.CreateProductUri(suggestion.StoreProductSlug));
		else
			NavigateToStore(StorePage.CreateSearchUri(suggestion.StoreQuery!));
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
				StoreProductSlug = result.ProductSlug,
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

	private void NavigateToStore(Uri uri)
	{
		NavControl.SelectedItem = StoreNavItem;
		if (ContentFrame.Content is StorePage storePage)
		{
			storePage.Open(uri);
			return;
		}

		_currentGameAppName = null;
		ContentFrame.Navigate(typeof(StorePage), uri, new EntranceNavigationTransitionInfo());
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

		NavControl_Navigate(Type.GetType(tag), args.RecommendedNavigationTransitionInfo);
	}

	private void NavigateToGame(string appName, NavigationTransitionInfo transitionInfo)
	{
		if (Equals(ContentFrame.CurrentSourcePageType, typeof(GameInfoPage)) &&
			string.Equals(_currentGameAppName, appName, StringComparison.Ordinal))
			return;

		_currentGameAppName = appName;
		ContentFrame.Navigate(typeof(GameInfoPage), appName, transitionInfo);
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
		var installed = _libraryCache
			.Where(game => game.Metadata is not null && !game.IsDlc())
			.Where(game => IsPaneListed(game, activeAppName))
			.OrderBy(game => game.AppTitle, StringComparer.CurrentCultureIgnoreCase)
			.ToList();

		var snapshot = installed
			.Select(game => (game.AppName, game.AppTitle, game.LocalAppState?.InstallStatus))
			.ToList();
		if (!force && snapshot.SequenceEqual(_installedMenuSnapshot) &&
			string.Equals(activeAppName, _installedMenuActiveAppName, StringComparison.Ordinal))
			return;
		_installedMenuSnapshot = snapshot;
		_installedMenuActiveAppName = activeAppName;
		_gameNavEntries.Clear();

		for (var i = NavControl.MenuItems.Count - 1; i >= 0; i--)
		{
			if (NavControl.MenuItems[i] is NavigationViewItem { Tag: string tag } &&
				tag.StartsWith(GameNavTagPrefix, StringComparison.Ordinal))
				NavControl.MenuItems.RemoveAt(i);
		}

		foreach (var game in installed)
			NavControl.MenuItems.Add(CreateInstalledGameItem(game));

		ApplyInstallVisual(_activeInstall);
	}

	private static bool IsPaneListed(Game game, string? activeAppName) =>
		string.Equals(game.AppName, activeAppName, StringComparison.Ordinal) ||
		game.LocalAppState?.InstallStatus is InstallState.Installed or InstallState.NeedUpdate
			or InstallState.Installing or InstallState.InstallationPaused
			or InstallState.Updating or InstallState.UpdatingPaused
			or InstallState.Repairing;

	private NavigationViewItem CreateInstalledGameItem(Game game)
	{
		var appName = game.AppName;
		var title = string.IsNullOrWhiteSpace(game.AppTitle) ? appName : game.AppTitle;
		var canLaunch = game.LocalAppState?.InstallStatus is InstallState.Installed or InstallState.NeedUpdate;
		var isActive = string.Equals(appName, _activeInstall?.AppName, StringComparison.Ordinal);

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
			Content = new FontIcon { Glyph = "\uE768", FontSize = 12 },
			Padding = new Thickness(6, 0, 6, 0),
			Margin = new Thickness(4, 0, 0, 0),
			VerticalAlignment = VerticalAlignment.Center,
			Visibility = canLaunch ? Visibility.Visible : Visibility.Collapsed,
			Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
			BorderThickness = new Thickness(0)
		};
		ToolTipService.SetToolTip(playButton, $"Play {title}");
		playButton.Click += (_, _) => LaunchGame(appName, title);
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
			_activeInstall = IsActionRunning(installItem) ? installItem : null;
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

	private static IconElement? CreateGameIcon(Game game)
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

		return new ImageIcon { Source = bitmap, Width = GameIconBoxHeight, Height = GameIconBoxHeight };
	}

	private void LaunchGame(string appName, string title)
	{
		_log.Information("MainWindow: Launching {Title} ({AppName}) from the navigation pane", title, appName);
		_ = Task.Run(() => _libraryManager.LaunchApp(appName));
	}

	private void OnDownloadSummaryChanged(DownloadSummary summary)
	{
		_lastDownloadSummary = summary;
		ToolTipService.SetToolTip(DownloadsNavItem, summary.ToolTip);
		DownloadInfoBadge.Value = summary.ProgressPercentage;
		UpdateBadgeVisibility();
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

				_installManager.LoadPendingInstalls();

				Task.Run(async () =>
				{
					try
					{
						await _libraryManager.GetLibraryData();
					}
					catch (Exception ex)
					{
						_log.Error(ex, "MainWindow: could not load the installed games list");
					}
				});
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
	private bool TrySetSystemBackdrop()
	{
		if (!MicaController.IsSupported())
			return false; // Mica is not supported on this system
		_mWsdqHelper = new WindowsSystemDispatcherQueueHelper();
		_mWsdqHelper.EnsureWindowsSystemDispatcherQueueController();

		// Create the policy object.
		_mConfigurationSource = new SystemBackdropConfiguration();
		this.Activated += Window_Activated;
		this.Closed += Window_Closed;
		((FrameworkElement)this.Content).ActualThemeChanged += Window_ThemeChanged;

		// Initial configuration state.
		_mConfigurationSource.IsInputActive = true;
		SetConfigurationSourceTheme();

		_mBackdropController = new MicaController();

		// Enable the system backdrop.
		// Note: Be sure to have "using WinRT;" to support the Window.As<...>() call.
		_mBackdropController.AddSystemBackdropTarget(this.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
		_mBackdropController.SetSystemBackdropConfiguration(_mConfigurationSource);
		return true; // succeeded

	}

	private void Window_Activated(object sender, WindowActivatedEventArgs args)
	{
		_mConfigurationSource.IsInputActive = args.WindowActivationState != WindowActivationState.Deactivated;
	}

	private void Window_Closed(object sender, WindowEventArgs args)
	{
		// Make sure any Mica/Acrylic controller is disposed
		// so it doesn't try to use this closed window.
		if (_mBackdropController != null)
		{
			_mBackdropController.Dispose();
			_mBackdropController = null;
		}
		this.Activated -= Window_Activated;
		_mConfigurationSource = null;

		_libraryManager.LibraryUpdated -= LibraryUpdatedHandler;
		_libraryManager.GameStatusUpdated -= GameStatusUpdatedHandler;
		CancelStoreSearch();
		CurrentDownload.SummaryChanged -= OnDownloadSummaryChanged;
		_installManager.InstallationStatusChanged -= InstallationStatusChangedHandler;
		_installManager.InstallProgressUpdate -= InstallProgressUpdateHandler;
		_taskbarProgress?.Clear();
	}

	private void Window_ThemeChanged(FrameworkElement sender, object args)
	{
		if (_mConfigurationSource != null)
		{
			SetConfigurationSourceTheme();
		}
	}

	private void SetConfigurationSourceTheme()
	{
		switch (((FrameworkElement)this.Content).ActualTheme)
		{
			case ElementTheme.Dark: _mConfigurationSource.Theme = SystemBackdropTheme.Dark; break;
			case ElementTheme.Light: _mConfigurationSource.Theme = SystemBackdropTheme.Light; break;
			case ElementTheme.Default: _mConfigurationSource.Theme = SystemBackdropTheme.Default; break;
		}
	}
}

internal class WindowsSystemDispatcherQueueHelper
{
	[StructLayout(LayoutKind.Sequential)]
	private struct DispatcherQueueOptions
	{
		internal int dwSize;
		internal int threadType;
		internal int apartmentType;
	}

	[DllImport("CoreMessaging.dll")]
	private static extern int CreateDispatcherQueueController([In] DispatcherQueueOptions options, [In, Out, MarshalAs(UnmanagedType.IUnknown)] ref object dispatcherQueueController);

	private object _mDispatcherQueueController = null;
	public void EnsureWindowsSystemDispatcherQueueController()
	{
		if (Windows.System.DispatcherQueue.GetForCurrentThread() != null)
		{
			// one already exists, so we'll just use it.
			return;
		}

		if (_mDispatcherQueueController != null) return;
		DispatcherQueueOptions options;
		options.dwSize = Marshal.SizeOf(typeof(DispatcherQueueOptions));
		options.threadType = 2;	// DQTYPE_THREAD_CURRENT
		options.apartmentType = 2; // DQTAT_COM_STA

		CreateDispatcherQueueController(options, ref _mDispatcherQueueController);
	}
}


