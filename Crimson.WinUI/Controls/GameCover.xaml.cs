using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Utils;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Crimson.Controls;

public sealed partial class GameCover : UserControl
{
	// Covers are shown at 216x290; twice that keeps them sharp on high-DPI screens.
	private const int RequestWidth = 432;
	private const int RequestHeight = 580;
	private const int DisplayWidth = 216;
	private const int DisplayHeight = 290;

	private static ImageCache? _cache;
	private static ImageCache Cache => _cache ??= App.GetService<ImageCache>();

	private int _loadVersion;
	private CancellationTokenSource? _loadCancellation;

	public static readonly DependencyProperty ImageUrlProperty = DependencyProperty.Register(
		nameof(ImageUrl),
		typeof(string),
		typeof(GameCover),
		new PropertyMetadata(null, OnImageUrlChanged));

	public static readonly DependencyProperty ColorRevealProperty = DependencyProperty.Register(
		nameof(ColorReveal),
		typeof(double),
		typeof(GameCover),
		new PropertyMetadata(0d, OnColorRevealChanged));

	public GameCover()
	{
		InitializeComponent();
	}

	public string? ImageUrl
	{
		get => (string?)GetValue(ImageUrlProperty);
		set => SetValue(ImageUrlProperty, value);
	}

	public double ColorReveal
	{
		get => (double)GetValue(ColorRevealProperty);
		set => SetValue(ColorRevealProperty, value);
	}

	private static void OnImageUrlChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
	{
		var cover = (GameCover)sender;
		cover.ApplyImageUrl(args.NewValue as string);
	}

	private static void OnColorRevealChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
		((GameCover)sender).UpdateClip();

	private void ApplyImageUrl(string? url)
	{
		var version = ++_loadVersion;
		// A recycled card drops the request for the cover it showed before, so scrolling quickly
		// does not leave the visible cards waiting behind ones that are no longer on screen.
		_loadCancellation?.Cancel();
		_loadCancellation = null;
		ColorImage.Source = null;
		GrayImage.Source = null;
		MutedOverlay.Visibility = Visibility.Collapsed;
		UpdateClip();

		if (string.IsNullOrWhiteSpace(url) ||
			!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
			!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
			return;

		_loadCancellation = new CancellationTokenSource();
		_ = LoadAsync(url, version, _loadCancellation.Token);
	}

	private async Task LoadAsync(string url, int version, CancellationToken cancellationToken)
	{
		string path;
		try
		{
			path = await GetCachedFileAsync(url, cancellationToken);
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch
		{
			if (version == _loadVersion)
				MutedOverlay.Visibility = Visibility.Visible;
			return;
		}

		if (version != _loadVersion)
			return;

		try
		{
			var color = new BitmapImage
			{
				DecodePixelType = DecodePixelType.Logical,
				DecodePixelWidth = DisplayWidth,
				DecodePixelHeight = DisplayHeight
			};
			using (var stream = File.OpenRead(path).AsRandomAccessStream())
				await color.SetSourceAsync(stream);
			if (version != _loadVersion)
				return;
			ColorImage.Source = color;

			var gray = await CreateGrayscaleAsync(path);
			if (version == _loadVersion)
				GrayImage.Source = gray;
		}
		catch
		{
			// If the cover cannot be decoded, the muted overlay keeps the card reading as
			// unavailable instead of showing a broken image.
			if (version == _loadVersion)
				MutedOverlay.Visibility = Visibility.Visible;
		}
	}

	// Original artwork rather than a CDN-resized variant: Epic resizes on a cache miss, which
	// takes seconds per cover, while originals are served from the edge immediately.
	private static Task<string> GetCachedFileAsync(string url, CancellationToken cancellationToken) =>
		Cache.GetAsync(url, cancellationToken);

	private static async Task<SoftwareBitmapSource> CreateGrayscaleAsync(string path)
	{
		using var stream = File.OpenRead(path).AsRandomAccessStream();
		var decoder = await BitmapDecoder.CreateAsync(stream);
		var transform = new BitmapTransform
		{
			ScaledWidth = RequestWidth,
			ScaledHeight = RequestHeight,
			InterpolationMode = BitmapInterpolationMode.Fant
		};
		using var source = await decoder.GetSoftwareBitmapAsync(
			BitmapPixelFormat.Bgra8,
			BitmapAlphaMode.Premultiplied,
			transform,
			ExifOrientationMode.IgnoreExifOrientation,
			ColorManagementMode.DoNotColorManage);

		var bytes = new byte[source.PixelWidth * source.PixelHeight * 4];
		var buffer = new Windows.Storage.Streams.Buffer((uint)bytes.Length) { Length = (uint)bytes.Length };
		source.CopyToBuffer(buffer);
		using (var reader = DataReader.FromBuffer(buffer))
			reader.ReadBytes(bytes);

		for (var i = 0; i < bytes.Length; i += 4)
		{
			var luminance = (byte)((bytes[i] * 114 + bytes[i + 1] * 587 + bytes[i + 2] * 299) / 1000);
			bytes[i] = luminance;
			bytes[i + 1] = luminance;
			bytes[i + 2] = luminance;
		}

		using var destination = new SoftwareBitmap(
			BitmapPixelFormat.Bgra8, source.PixelWidth, source.PixelHeight, BitmapAlphaMode.Premultiplied);
		using (var writer = new DataWriter())
		{
			writer.WriteBytes(bytes);
			destination.CopyFromBuffer(writer.DetachBuffer());
		}

		// A fresh SoftwareBitmapSource per card: those hold composition surfaces that become
		// invalid once the page that created them is torn down, so they must not be shared.
		var imageSource = new SoftwareBitmapSource();
		await imageSource.SetBitmapAsync(destination);
		return imageSource;
	}

	private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateClip();

	private void UpdateClip()
	{
		if (Root.ActualWidth <= 0 || Root.ActualHeight <= 0)
			return;

		var reveal = Math.Clamp(ColorReveal, 0d, 1d);
		ColorClip.Rect = new Rect(0, 0, Root.ActualWidth * reveal, Root.ActualHeight);
	}
}
