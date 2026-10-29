using Crimson.Utils;

namespace Crimson.Tests;

public sealed class StorageSizeFormatterTests
{
	[Theory]
	[InlineData(512, "512.00 MB")]
	[InlineData(1024, "1.00 GB")]
	[InlineData(1536, "1.50 GB")]
	public void FormatsMebibytes(double value, string expected)
	{
		Assert.Equal(expected, StorageSizeFormatter.FormatMebibytes(value));
	}
}
