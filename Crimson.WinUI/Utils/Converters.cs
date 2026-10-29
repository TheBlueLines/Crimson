using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml;
using CommunityToolkit.WinUI.Converters;
using Crimson.ViewModels;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Crimson.Utils;

public class BoolToVisibilityConverter : BoolToObjectConverter
{
	public BoolToVisibilityConverter()
	{
		TrueValue = Visibility.Visible;
		FalseValue = Visibility.Collapsed;
	}
}

public class BoolToInverseVisibilityConverter : BoolToObjectConverter
{
	/// <summary>
	/// Initializes a new instance of the <see cref="BoolToInverseVisibilityConverter"/> class.
	/// </summary>
	public BoolToInverseVisibilityConverter()
	{
		TrueValue = Visibility.Collapsed;
		FalseValue = Visibility.Visible;
	}
}

public class ImageUrlConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, string language)
	{
		return value is string imageUrl && !string.IsNullOrWhiteSpace(imageUrl)
			? new BitmapImage(new Uri(imageUrl))
			: null!;
	}

	public object ConvertBack(object value, Type targetType, object parameter, string language)
	{
		throw new NotImplementedException();
	}
}

/// <summary>
/// Store art is often 1200x1600 or larger; decoding at the displayed width keeps a full
/// results grid from holding dozens of full-size bitmaps. The parameter is that width.
/// </summary>
public class DecodedImageUrlConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, string language)
	{
		if (value is not string imageUrl ||
			!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) ||
			!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
			return null!;

		var image = new BitmapImage { DecodePixelType = DecodePixelType.Logical };
		if (int.TryParse(parameter as string, out var width) && width > 0)
			image.DecodePixelWidth = width;
		image.UriSource = uri;
		return image;
	}

	public object ConvertBack(object value, Type targetType, object parameter, string language)
	{
		throw new NotImplementedException();
	}
}

public class StoreProductActionGlyphConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, string language) => value switch
	{
		StoreProductAction.Play => "",
		StoreProductAction.Install => "",
		StoreProductAction.Update => "",
		StoreProductAction.Repair => "",
		StoreProductAction.Get or StoreProductAction.Buy => "",
		StoreProductAction.Redeem => "",
		StoreProductAction.ViewOnWeb => "",
		_ => ""
	};

	public object ConvertBack(object value, Type targetType, object parameter, string language)
	{
		throw new NotImplementedException();
	}
}

public class GamePrimaryActionGlyphConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, string language) => value switch
	{
		GamePrimaryAction.Install => "\uE896",
		GamePrimaryAction.Play => "\uE768",
		GamePrimaryAction.Update => "\uE777",
		GamePrimaryAction.Repair => "\uE90F",
		GamePrimaryAction.Stop => "\uE71A",
		_ => string.Empty
	};

	public object ConvertBack(object value, Type targetType, object parameter, string language)
	{
		throw new NotImplementedException();
	}
}

public class DriveSpaceColorConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, string language)
	{
		if (value is bool canInstall)
		{
			// Get colors from theme resources to respect light/dark mode
			if (canInstall)
			{
				return (SolidColorBrush)Application.Current.Resources["SystemFillColorSuccessBrush"];
			}
			else
			{
				return (SolidColorBrush)Application.Current.Resources["SystemFillColorCriticalBrush"];
			}
		}

		// Return default color if input is invalid
		return (SolidColorBrush)Application.Current.Resources["TextFillColorPrimaryBrush"];
	}

	public object ConvertBack(object value, Type targetType, object parameter, string language)
	{
		throw new NotImplementedException();
	}
}
