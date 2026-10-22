using System.Numerics;
using System.Reflection;
using Crimson.Core;
using Crimson.Models;
using Serilog;

namespace Crimson.Tests;

public sealed class InstallerUpdateTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"crimson-update-{Guid.NewGuid():N}");
	private readonly Serilog.Core.Logger _logger = new LoggerConfiguration().CreateLogger();
	private readonly InstallManager _installer;

	public InstallerUpdateTests()
	{
		Directory.CreateDirectory(_root);
		_installer = new InstallManager(_logger, null!, null!, null!, null!, null!, null!);
	}

	[Fact]
	public async Task CopyTaskTruncatesAChangedFileToManifestLength()
	{
		var destination = Path.Combine(_root, "changed.bin");
		await File.WriteAllBytesAsync(destination, [1, 2, 3, 4, 5]);
		var chunkPath = Path.Combine(_root, "chunk.bin");
		await File.WriteAllBytesAsync(chunkPath, CreateUncompressedChunk([9, 8, 7]));
		var task = new IoTask {
			SourceFilePath = chunkPath,
			DestinationFilePath = destination,
			TaskType = IoTaskType.Copy,
			Size = 3,
			DestinationFileSize = 3,
			GuidNum = BigInteger.Zero,
			SourceChunkGuidNum = BigInteger.Zero
		};
		var method = typeof(InstallManager).GetMethod("ProcessCopyTask", BindingFlags.Instance | BindingFlags.NonPublic)!;

		await ((Task)method.Invoke(_installer, [task, _root])!).WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(destination));
	}

	[Fact]
	public void VerificationFailureCannotReportSuccess()
	{
		var install = new InstallItem("test", ActionType.Update, _root);
		var method = typeof(InstallManager).GetMethod("EnsureVerificationSucceeded", BindingFlags.Static | BindingFlags.NonPublic)!;

		var exception = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [install, 2]));

		Assert.IsType<InvalidDataException>(exception.InnerException);
		Assert.Equal("2 files failed verification", install.StatusMessage);
	}

	private static byte[] CreateUncompressedChunk(byte[] payload)
	{
		using var stream = new MemoryStream();
		using var writer = new BinaryWriter(stream);
		writer.Write(0xB1FE3AA2u);
		writer.Write(3u);
		writer.Write(66u);
		writer.Write((uint)payload.Length);
		for (var index = 0; index < 4; index++) writer.Write(0u);
		writer.Write(0UL);
		writer.Write((byte)0);
		writer.Write(new byte[20]);
		writer.Write((byte)0);
		writer.Write((uint)payload.Length);
		writer.Write(payload);
		return stream.ToArray();
	}

	public void Dispose()
	{
		_logger.Dispose();
		if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
	}
}
