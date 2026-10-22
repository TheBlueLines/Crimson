using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Crimson.Models;
using Crimson.Utils;

namespace Crimson.Tests;

public sealed class ParserAdversaryTests
{
	[Fact]
	public void ManifestRead_RejectsEveryTruncatedPrefix()
	{
		var valid = BuildManifest(Encoding.UTF8.GetBytes("bounded manifest"));

		for (var length = 0; length < valid.Length; length++)
			AssertRejected(() => Manifest.Read(valid[..length]));
	}

	[Fact]
	public void ManifestRead_RejectsTrailingDataAndOutOfBandVersion()
	{
		var valid = BuildManifest(Encoding.UTF8.GetBytes("bounded manifest"));
		var withTrailingData = valid.Concat(new byte[] { 0xFF }).ToArray();

		// The envelope is fixed size, so trailing bytes there are still corruption.
		Assert.Throws<InvalidDataException>(() => Manifest.Read(withTrailingData));

		// Feature levels 12-24 are supported (legendary's range); only outside that is rejected.
		foreach (var supported in new[] { 12, 17, 21, 22, 24 })
		{
			var candidate = valid.ToArray();
			BitConverter.GetBytes(supported).CopyTo(candidate, 37);
			Manifest.Read(candidate);
		}

		foreach (var unsupported in new[] { 11, 25 })
		{
			var candidate = valid.ToArray();
			BitConverter.GetBytes(unsupported).CopyTo(candidate, 37);
			Assert.Throws<InvalidDataException>(() => Manifest.Read(candidate));
		}
	}

	[Fact]
	public void ManifestRead_RejectsOversizedDeclarationAndCompressionBombRatio()
	{
		var oversized = BuildManifest([1]);
		BitConverter.GetBytes(512 * 1024 * 1024 + 1).CopyTo(oversized, 8);
		var compressionBomb = BuildManifest(new byte[1024 * 1024], compressed: true);

		Assert.Throws<InvalidDataException>(() => Manifest.Read(oversized));
		Assert.Throws<InvalidDataException>(() => Manifest.Read(compressionBomb));
	}

	[Fact]
	public void ChunkReadBuffer_RejectsTruncationCorruptionAndTrailingData()
	{
		var valid = BuildChunk(Encoding.UTF8.GetBytes("integrity checked chunk"), compressed: true);
		for (var length = 0; length < valid.Length; length++)
			AssertRejected(() => Chunk.ReadBuffer(valid[..length]));

		var trailing = valid.Concat(new byte[] { 0x00 }).ToArray();
		var wrongCompressedSize = valid.ToArray();
		BitConverter.GetBytes(BitConverter.ToUInt32(valid, 12) + 1).CopyTo(wrongCompressedSize, 12);

		Assert.Throws<InvalidDataException>(() => Chunk.ReadBuffer(trailing));
		AssertRejected(() => Chunk.ReadBuffer(wrongCompressedSize));

		// Corruption in a compressed payload is still caught, by the zlib checksum rather than
		// by a manifest hash comparison. The parser deliberately does not verify the stored
		// rolling/SHA-1 hashes on read: Epic computes those over the payload padded to a full
		// 1 MiB window and only populates them according to HashType, so checking them here
		// would reject valid chunks. ValidateAgainst is the explicit integrity check.
		var corrupted = valid.ToArray();
		corrupted[^1] ^= 0x80;
		Assert.Throws<InvalidDataException>(() => Chunk.ReadBuffer(corrupted));
	}

	[Fact]
	public void ChunkReadBuffer_AcceptsLegitimateHighlyCompressiblePayload()
	{
		var payload = new byte[1024 * 1024];
		var serialized = BuildChunk(payload, compressed: true);

		Assert.True(payload.Length > (serialized.Length - 66) * 100);
		Assert.Equal(payload, Chunk.ReadBuffer(serialized).Data);
	}

	[Fact]
	public void ChunkValidateAgainst_RejectsManifestIdentityMismatch()
	{
		var payload = Encoding.UTF8.GetBytes("chunk identity");
		var chunk = Chunk.ReadBuffer(BuildChunk(payload, compressed: false));
		var expected = new ChunkInfo(18)
		{
			Guid = [1, 2, 3, 5],
			Hash = unchecked((long)RollingHash.ComputeHash(payload)),
			ShaHash = SHA1.HashData(payload),
			WindowSize = payload.Length,
			FileSize = payload.Length + 66
		};

		Assert.Throws<InvalidDataException>(() => chunk.ValidateAgainst(expected));
	}

	[Fact]
	public void ChunkValidateAgainst_AcceptsUnsignedGuidComponents()
	{
		var payload = Encoding.UTF8.GetBytes("unsigned chunk identity");
		var data = BuildChunk(payload, compressed: false);
		uint[] guid = [0xFEF822C6, 0x4BE2C4CE, 0x9644BF8D, 0x427F2F13];
		for (var index = 0; index < guid.Length; index++)
			BitConverter.GetBytes(guid[index]).CopyTo(data, 16 + index * sizeof(uint));

		var expectedGuid = guid.Select(value => unchecked((int)value)).ToArray();
		var expected = new ChunkInfo(21)
		{
			Guid = expectedGuid,
			Hash = unchecked((long)RollingHash.ComputeHash(payload)),
			ShaHash = SHA1.HashData(payload),
			WindowSize = payload.Length,
			FileSize = payload.Length + 66
		};
		var part = new ChunkPart(expectedGuid, 0, payload.Length);
		var chunk = Chunk.ReadBuffer(data);

		chunk.ValidateAgainst(expected);
		Assert.Equal(chunk.GuidNum, expected.GuidNum);
		Assert.Equal(expected.GuidNum, part.GuidNum);
	}

	[Fact]
	public void SectionParsers_RejectNegativeCounts()
	{
		Assert.Throws<InvalidDataException>(() => CDL.Read(BuildSectionWithCount(-1)));
		Assert.Throws<InvalidDataException>(() => FileManifestList.Read(BuildSectionWithCount(-1)));
		Assert.Throws<InvalidDataException>(() => CustomFields.Read(BuildSectionWithCount(-1)));
	}

	[Fact]
	public void ManifestIntegrity_ValidatesSha1AndSha256Digests()
	{
		var data = Encoding.UTF8.GetBytes("trusted manifest bytes");

		ManifestIntegrity.VerifyDigest(data, Convert.ToHexString(SHA1.HashData(data)));
		ManifestIntegrity.VerifyDigest(data, $"sha256:{Convert.ToHexString(SHA256.HashData(data))}");
		Assert.Throws<InvalidDataException>(() =>
			ManifestIntegrity.VerifyDigest(data, new string('0', 40)));
		Assert.Throws<InvalidDataException>(() =>
			ManifestIntegrity.VerifyDigest(data, "not-a-digest"));
	}

	[Fact]
	public void RandomMalformedInputs_FailWithinBoundedBudget()
	{
		var random = new Random(0x4352494D);
		var stopwatch = Stopwatch.StartNew();
		for (var iteration = 0; iteration < 2_000; iteration++)
		{
			var bytes = new byte[random.Next(0, 513)];
			random.NextBytes(bytes);
			AssertRejected(() => Manifest.Read(bytes));
			AssertRejected(() => Chunk.ReadBuffer(bytes));
		}

		Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Malformed input budget exceeded: {stopwatch.Elapsed}.");
	}

	private static MemoryStream BuildSectionWithCount(int count)
	{
		var stream = new MemoryStream();
		using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
		{
			writer.Write(9);
			writer.Write((byte)0);
			writer.Write(count);
		}
		stream.Position = 0;
		return stream;
	}

	private static MemoryStream BuildDuplicatePathSection()
	{
		var stream = new MemoryStream();
		using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
		writer.Write(0);
		writer.Write((byte)0);
		writer.Write(2);
		WriteString(writer, "Data/File.bin");
		WriteString(writer, "data/file.BIN");
		writer.Flush();
		stream.Position = 0;
		writer.Write(checked((int)stream.Length));
		writer.Flush();
		stream.Position = 0;
		return stream;
	}

	private static byte[] BuildManifest(byte[] payload, bool compressed = false)
	{
		var storedPayload = compressed ? Compress(payload) : payload;
		using var stream = new MemoryStream();
		using var writer = new BinaryWriter(stream);
		writer.Write(0x44BEC00Cu);
		writer.Write(41);
		writer.Write(payload.Length);
		writer.Write(storedPayload.Length);
		writer.Write(SHA1.HashData(payload));
		writer.Write((byte)(compressed ? 1 : 0));
		writer.Write(21);
		writer.Write(storedPayload);
		return stream.ToArray();
	}

	private static byte[] BuildChunk(byte[] payload, bool compressed)
	{
		var storedPayload = compressed ? Compress(payload) : payload;
		using var stream = new MemoryStream();
		using var writer = new BinaryWriter(stream);
		writer.Write(0xB1FE3AA2u);
		writer.Write(3u);
		writer.Write(66u);
		writer.Write((uint)storedPayload.Length);
		writer.Write(1u);
		writer.Write(2u);
		writer.Write(3u);
		writer.Write(4u);
		writer.Write(RollingHash.ComputeHash(payload));
		writer.Write((byte)(compressed ? 1 : 0));
		writer.Write(SHA1.HashData(payload));
		writer.Write((byte)2);
		writer.Write((uint)payload.Length);
		writer.Write(storedPayload);
		return stream.ToArray();
	}

	private static byte[] Compress(byte[] payload)
	{
		using var output = new MemoryStream();
		using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
			zlib.Write(payload);
		return output.ToArray();
	}

	private static void WriteString(BinaryWriter writer, string value)
	{
		var bytes = Encoding.UTF8.GetBytes(value + '\0');
		writer.Write(bytes.Length);
		writer.Write(bytes);
	}

	private static void AssertRejected(Action action)
	{
		var exception = Record.Exception(action);
		Assert.NotNull(exception);
		Assert.True(
			exception is InvalidDataException or EndOfStreamException,
			$"Unexpected exception type: {exception.GetType().FullName}");
	}
}
