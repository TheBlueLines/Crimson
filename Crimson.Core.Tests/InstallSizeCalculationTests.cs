using Crimson.Core;
using Crimson.Models;

namespace Crimson.Tests;

public sealed class InstallSizeCalculationTests
{
	[Fact]
	public void SharedChunksCountOnceForDownloadAndPerFileForWrite()
	{
		var sharedGuid = new[] { 1, 2, 3, 4 };
		var uniqueGuid = new[] { 5, 6, 7, 8 };
		var sharedPart = new ChunkPart(sharedGuid, size: 20);
		var secondSharedPart = new ChunkPart(sharedGuid, size: 30);
		var uniquePart = new ChunkPart(uniqueGuid, size: 40);
		var files = new FileManifestList();
		var firstFile = new FileManifest { Filename = "first", FileSize = 20 };
		firstFile.ChunkParts.Add(sharedPart);
		var secondFile = new FileManifest { Filename = "second", FileSize = 70 };
		secondFile.ChunkParts.Add(secondSharedPart);
		secondFile.ChunkParts.Add(uniquePart);
		files.Elements.Add(firstFile);
		files.Elements.Add(secondFile);

		var chunks = new CDL();
		chunks.Elements.Add(new ChunkInfo { Guid = sharedGuid, FileSize = 100 });
		chunks.Elements.Add(new ChunkInfo { Guid = uniqueGuid, FileSize = 200 });
		var manifest = new Manifest { CDL = chunks, FileManifestList = files };

		var sizes = InstallManager.CalculateManifestSizes(manifest);

		Assert.Equal(300, sizes.DownloadBytes);
		Assert.Equal(90, sizes.WriteBytes);
	}
}
