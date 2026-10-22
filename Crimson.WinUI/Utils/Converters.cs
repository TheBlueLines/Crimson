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

public class GamePrimaryActionGlyphConverter : IValueConverter
{
	public object Convert(object value, Type targetType, object parameter, string language) => value switch
	{
		GamePrimaryAction.Install => "\uE896",
		GamePrimaryAction.Play => "\uE768",
		GamePrimaryAction.Update => "\uE777",
		GamePrimaryAction.Repair => "\uE90F",
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
				return Application.Current.Resources["SystemFillColorSuccessBrush"] as SolidColorBrush;
			}
			else
			{
				return Application.Current.Resources["SystemFillColorCriticalBrush"] as SolidColorBrush;
			}
		}

		// Return default color if input is invalid
		return Application.Current.Resources["TextFillColorPrimaryBrush"] as SolidColorBrush;
	}

	public object ConvertBack(object value, Type targetType, object parameter, string language)
	{
		throw new NotImplementedException();
	}
}
