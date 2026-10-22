using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Crimson.Controls;

public sealed partial class GameCover : UserControl
{
	private static readonly ConcurrentDictionary<string, SoftwareBitmapSource> GrayscaleCache = new();

	private int _loadVersion;

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
		ColorImage.Source = CreateImage(url);
		GrayImage.Source = null;
		MutedOverlay.Visibility = string.IsNullOrWhiteSpace(url) ? Visibility.Collapsed : Visibility.Visible;
		UpdateClip();

		if (!string.IsNullOrWhiteSpace(url))
			_ = LoadGrayscaleAsync(url, version);
	}

	private async Task LoadGrayscaleAsync(string url, int version)
	{
		try
		{
			if (GrayscaleCache.TryGetValue(url, out var cached))
			{
				if (version == _loadVersion)
					ApplyGrayscale(cached);
				return;
			}

			using var stream = await RandomAccessStreamReference.CreateFromUri(new Uri(url)).OpenReadAsync();
			var decoder = await BitmapDecoder.CreateAsync(stream);
			var transform = new BitmapTransform
			{
				ScaledWidth = 320,
				ScaledHeight = 430,
				InterpolationMode = BitmapInterpolationMode.Fant
			};
			using var source = await decoder.GetSoftwareBitmapAsync(
				BitmapPixelFormat.Bgra8,
				BitmapAlphaMode.Premultiplied,
				transform,
				ExifOrientationMode.IgnoreExifOrientation,
				ColorManagementMode.DoNotColorManage);

			var width = source.PixelWidth;
			var height = source.PixelHeight;
			var bytes = new byte[width * height * 4];
			var inputBuffer = new Windows.Storage.Streams.Buffer((uint)bytes.Length) { Length = (uint)bytes.Length };
			source.CopyToBuffer(inputBuffer);
			using (var reader = DataReader.FromBuffer(inputBuffer))
				reader.ReadBytes(bytes);

			for (var i = 0; i < bytes.Length; i += 4)
			{
				var luminance = (byte)((bytes[i] * 114 + bytes[i + 1] * 587 + bytes[i + 2] * 299) / 1000);
				bytes[i] = luminance;
				bytes[i + 1] = luminance;
				bytes[i + 2] = luminance;
			}

			using var destination = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
			using (var writer = new DataWriter())
			{
				writer.WriteBytes(bytes);
				destination.CopyFromBuffer(writer.DetachBuffer());
			}

			var imageSource = new SoftwareBitmapSource();
			await imageSource.SetBitmapAsync(destination);
			GrayscaleCache.TryAdd(url, imageSource);

			if (version == _loadVersion)
				ApplyGrayscale(imageSource);
		}
		catch
		{
			// If the cover cannot be decoded, keep the muted overlay so the card still
			// reads as unavailable instead of showing a broken image.
		}
	}

	private void ApplyGrayscale(SoftwareBitmapSource imageSource)
	{
		GrayImage.Source = imageSource;
		MutedOverlay.Visibility = Visibility.Collapsed;
	}

	private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateClip();

	private void UpdateClip()
	{
		if (Root.ActualWidth <= 0 || Root.ActualHeight <= 0)
			return;

		var reveal = Math.Clamp(ColorReveal, 0d, 1d);
		ColorClip.Rect = new Rect(0, 0, Root.ActualWidth * reveal, Root.ActualHeight);
	}

	private static ImageSource? CreateImage(string? url)
	{
		if (string.IsNullOrWhiteSpace(url) ||
			!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
			!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
			return null;

		return new BitmapImage
		{
			DecodePixelType = DecodePixelType.Logical,
			DecodePixelWidth = 216,
			DecodePixelHeight = 290,
			UriSource = uri
		};
	}
}
