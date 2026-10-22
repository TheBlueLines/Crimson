using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Crimson.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Crimson.Views;

/// <summary>
/// Page where we list current and past downloads
/// </summary>
public sealed partial class DownloadsPage : Page
{
	private ObservableCollection<DownloadManagerItem>? _hookedQueue;
	private ObservableCollection<DownloadManagerItem>? _hookedHistory;

	public DownloadsViewModel ViewModel => (DownloadsViewModel)DataContext;

	public DownloadsPage()
	{
		InitializeComponent();
		DataContext = App.GetService<DownloadsViewModel>();
		ViewModel.PropertyChanged += ViewModel_PropertyChanged;
		_hookedQueue = ViewModel.QueueItems;
		_hookedHistory = ViewModel.HistoryItems;
		HookCollection(_hookedQueue, Queue_CollectionChanged);
		HookCollection(_hookedHistory, History_CollectionChanged);
		RefreshEmptyHints();
	}

	private void HookCollection(ObservableCollection<DownloadManagerItem>? target, NotifyCollectionChangedEventHandler handler)
	{
		if (target != null)
			target.CollectionChanged += handler;
	}

	private void UnhookCollection(ObservableCollection<DownloadManagerItem>? target, NotifyCollectionChangedEventHandler handler)
	{
		if (target != null)
			target.CollectionChanged -= handler;
	}

	private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(DownloadsViewModel.QueueItems))
		{
			UnhookCollection(_hookedQueue, Queue_CollectionChanged);
			_hookedQueue = ViewModel.QueueItems;
			HookCollection(_hookedQueue, Queue_CollectionChanged);
		}
		else if (e.PropertyName == nameof(DownloadsViewModel.HistoryItems))
		{
			UnhookCollection(_hookedHistory, History_CollectionChanged);
			_hookedHistory = ViewModel.HistoryItems;
			HookCollection(_hookedHistory, History_CollectionChanged);
		}

		RefreshEmptyHints();
	}

	private void Queue_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshEmptyHints();

	private void History_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshEmptyHints();

	private void RefreshEmptyHints()
	{
		QueueEmptyHint.Visibility = ViewModel.QueueItems is { Count: > 0 } ? Visibility.Collapsed : Visibility.Visible;
		HistoryEmptyHint.Visibility = ViewModel.HistoryItems is { Count: > 0 } ? Visibility.Collapsed : Visibility.Visible;
	}

	protected override void OnNavigatedFrom(NavigationEventArgs e)
	{
		ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
		UnhookCollection(_hookedQueue, Queue_CollectionChanged);
		UnhookCollection(_hookedHistory, History_CollectionChanged);
		ViewModel.Dispose();
		base.OnNavigatedFrom(e);
	}
}
