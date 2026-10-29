using System.Globalization;

namespace Crimson.Utils;

public static class StorageSizeFormatter
{
	public static string FormatMebibytes(double mebibytes)
	{
		var gibibytes = mebibytes / 1024;
		return gibibytes >= 1
			? $"{gibibytes.ToString("F2", CultureInfo.InvariantCulture)} GB"
			: $"{mebibytes.ToString("F2", CultureInfo.InvariantCulture)} MB";
	}

	public static string FormatSpeed(double mebibytesPerSecond) =>
		$"{mebibytesPerSecond.ToString("F1", CultureInfo.InvariantCulture)} MB/s";
}
