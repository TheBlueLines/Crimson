using Crimson.Utils;

namespace Crimson.Tests;

public sealed class FileHashTests
{
	[Fact]
	public void ComputesLowercaseSha1()
	{
		var path = Path.GetTempFileName();

		try
		{
			File.WriteAllText(path, "abc");

			Assert.Equal(
				"a9993e364706816aba3e25717850c26c9cd0d89d",
				FileHash.ComputeSha1(path));
		}
		finally
		{
			File.Delete(path);
		}
	}
}
