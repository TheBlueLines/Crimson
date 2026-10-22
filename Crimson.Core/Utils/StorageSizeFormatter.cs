namespace Crimson.Utils;

public static class StorageSizeFormatter
{
	public static string FormatMebibytes(double mebibytes)
	{
		var gibibytes = mebibytes / 1024;
		return gibibytes >= 1
			? $"{gibibytes:F2} GiB"
			: $"{mebibytes:F2} MiB";
	}
}
