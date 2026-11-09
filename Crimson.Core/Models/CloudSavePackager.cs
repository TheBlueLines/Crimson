using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Crimson.Utils;
using Ionic.Zlib;

namespace Crimson.Models;

internal sealed record CloudSaveUploadFile(string RemotePath, string TempFile);

internal sealed record CloudSavePackage(
	string ManifestName,
	CloudSaveUploadFile Manifest,
	IReadOnlyList<CloudSaveUploadFile> Chunks,
	string Fingerprint);

/// <summary>
/// Writes a save folder in the format Epic's cloud save service stores: 1 MiB zlib chunks plus a
/// binary manifest. The layout follows legendary's savegame_helper.py and model writers.
/// </summary>
internal static class CloudSavePackager
{
	public const int ChunkSize = 1024 * 1024;
	private const int ManifestVersion = 18;
	private const int ChunkHeaderSize = 66;
	private const int ChunkPartSize = 28;
	private const byte ChunkHashTypeRollingAndSha = 3;

	public static CloudSavePackage Package(
		string saveFolder,
		IEnumerable<string> relativeFiles,
		string appName,
		string accountId,
		string cloudFolder,
		string buildVersion,
		string tempDirectory)
	{
		Directory.CreateDirectory(tempDirectory);
		var chunkInfos = new List<ChunkInfo>();
		var chunks = new List<CloudSaveUploadFile>();
		var files = new List<FileManifest>();
		var buffer = new byte[ChunkSize];
		var guid = NewGuid();
		var filled = 0;

		void FinishChunk()
		{
			var (info, bytes) = BuildChunk(guid, buffer, filled);
			var tempFile = Path.Combine(tempDirectory, $"{chunks.Count:D6}.chunk");
			File.WriteAllBytes(tempFile, bytes);
			chunks.Add(new CloudSaveUploadFile(info.Path, tempFile));
			chunkInfos.Add(info);
			guid = NewGuid();
			filled = 0;
		}

		foreach (var relative in relativeFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
		{
			var file = new FileManifest { Filename = relative };
			using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
			using var stream = new FileStream(
				Path.Combine(saveFolder, relative),
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read);
			long fileOffset = 0;
			int read;
			while ((read = stream.Read(buffer, filled, ChunkSize - filled)) > 0)
			{
				hash.AppendData(buffer, filled, read);
				file.ChunkParts.Add(new ChunkPart(guid, filled, read) { FileOffset = fileOffset });
				fileOffset += read;
				filled += read;
				if (filled == ChunkSize)
					FinishChunk();
			}

			file.FileSize = fileOffset;
			file.Hash = hash.GetHashAndReset();
			files.Add(file);
		}

		if (filled > 0)
			FinishChunk();

		var manifestBytes = BuildManifest(
			$"{appName}{accountId}",
			buildVersion,
			cloudFolder,
			chunkInfos,
			files);
		var manifestFile = Path.Combine(tempDirectory, "save.manifest");
		File.WriteAllBytes(manifestFile, manifestBytes);
		var manifestName = $"{buildVersion}.manifest";
		return new CloudSavePackage(
			manifestName,
			new CloudSaveUploadFile($"manifests/{manifestName}", manifestFile),
			chunks,
			CloudSaveFingerprint.Compute(files.Select(file => (file.Filename, file.Hash))));
	}

	/// <summary>Builds one serialized chunk. Epic hashes the payload padded to the full window.</summary>
	internal static (ChunkInfo Info, byte[] Bytes) BuildChunk(int[] guid, byte[] buffer, int length)
	{
		Array.Clear(buffer, length, ChunkSize - length);
		var rollingHash = RollingHash.ComputeHash(buffer);
		var shaHash = SHA1.HashData(buffer);
		var compressed = Compress(buffer);

		using var output = new MemoryStream(ChunkHeaderSize + compressed.Length);
		using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
		writer.Write(Chunk.HeaderMagic);
		writer.Write(3u);
		writer.Write(ChunkHeaderSize);
		writer.Write(compressed.Length);
		foreach (var value in guid)
			writer.Write(value);
		writer.Write(rollingHash);
		writer.Write((byte)1);
		writer.Write(shaHash);
		writer.Write(ChunkHashTypeRollingAndSha);
		writer.Write(ChunkSize);
		writer.Write(compressed);
		writer.Flush();

		var info = new ChunkInfo
		{
			Guid = guid,
			Hash = unchecked((long)rollingHash),
			ShaHash = shaHash,
			WindowSize = ChunkSize,
			FileSize = output.Length
		};
		return (info, output.ToArray());
	}

	internal static byte[] BuildManifest(
		string manifestAppName,
		string buildVersion,
		string cloudFolder,
		IReadOnlyList<ChunkInfo> chunks,
		IReadOnlyList<FileManifest> files)
	{
		using var body = new MemoryStream();
		using (var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
		{
			WriteSection(writer, section => WriteMeta(section, manifestAppName, buildVersion));
			WriteSection(writer, section => WriteChunkList(section, chunks));
			WriteSection(writer, section => WriteFileList(section, files));
			WriteSection(writer, section =>
			{
				section.Write((byte)0);
				section.Write(1);
				WriteString(section, "CloudSaveFolder");
				WriteString(section, cloudFolder);
			});
		}

		var uncompressed = body.ToArray();
		var compressed = Compress(uncompressed);
		using var output = new MemoryStream();
		using var header = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
		header.Write(Manifest.HeaderMagic);
		header.Write(Manifest.ExpectedHeaderSize);
		header.Write(uncompressed.Length);
		header.Write(compressed.Length);
		header.Write(SHA1.HashData(uncompressed));
		header.Write((byte)1);
		header.Write(ManifestVersion);
		header.Write(compressed);
		header.Flush();
		return output.ToArray();
	}

	internal static int[] NewGuid()
	{
		var bytes = RandomNumberGenerator.GetBytes(16);
		return
		[
			BitConverter.ToInt32(bytes, 0),
			BitConverter.ToInt32(bytes, 4),
			BitConverter.ToInt32(bytes, 8),
			BitConverter.ToInt32(bytes, 12)
		];
	}

	private static void WriteMeta(BinaryWriter writer, string appName, string buildVersion)
	{
		writer.Write((byte)0);
		writer.Write((uint)ManifestVersion);
		writer.Write((byte)0);
		writer.Write(0u);
		WriteString(writer, appName);
		WriteString(writer, buildVersion);
		WriteString(writer, string.Empty);
		WriteString(writer, string.Empty);
		writer.Write(0u);
		WriteString(writer, string.Empty);
		WriteString(writer, string.Empty);
		WriteString(writer, string.Empty);
	}

	private static void WriteChunkList(BinaryWriter writer, IReadOnlyList<ChunkInfo> chunks)
	{
		writer.Write((byte)0);
		writer.Write(chunks.Count);
		foreach (var chunk in chunks)
			WriteGuid(writer, chunk.Guid);
		foreach (var chunk in chunks)
			writer.Write(chunk.Hash);
		foreach (var chunk in chunks)
			writer.Write(chunk.ShaHash);
		foreach (var chunk in chunks)
			writer.Write((byte)chunk.GroupNum);
		foreach (var chunk in chunks)
			writer.Write(chunk.WindowSize);
		foreach (var chunk in chunks)
			writer.Write(chunk.FileSize);
	}

	private static void WriteFileList(BinaryWriter writer, IReadOnlyList<FileManifest> files)
	{
		writer.Write((byte)0);
		writer.Write(files.Count);
		foreach (var file in files)
			WriteString(writer, file.Filename);
		foreach (var file in files)
			WriteString(writer, file.SymlinkTarget);
		foreach (var file in files)
			writer.Write(file.Hash);
		foreach (var file in files)
			writer.Write(file.Flags);
		foreach (var _ in files)
			writer.Write(0);
		foreach (var file in files)
		{
			writer.Write(file.ChunkParts.Count);
			foreach (var part in file.ChunkParts)
			{
				writer.Write(ChunkPartSize);
				WriteGuid(writer, part.Guid);
				writer.Write(part.Offset);
				writer.Write(part.Size);
			}
		}
	}

	private static void WriteGuid(BinaryWriter writer, int[] guid)
	{
		foreach (var value in guid)
			writer.Write(value);
	}

	private static void WriteSection(BinaryWriter writer, Action<BinaryWriter> content)
	{
		writer.Flush();
		var stream = writer.BaseStream;
		var start = stream.Position;
		writer.Write(0);
		content(writer);
		writer.Flush();
		var end = stream.Position;
		stream.Position = start;
		writer.Write(checked((int)(end - start)));
		writer.Flush();
		stream.Position = end;
	}

	private static void WriteString(BinaryWriter writer, string value)
	{
		if (value.Length == 0)
		{
			writer.Write(0);
			return;
		}

		if (value.All(char.IsAscii))
		{
			writer.Write(value.Length + 1);
			writer.Write(Encoding.ASCII.GetBytes(value));
			writer.Write((byte)0);
			return;
		}

		writer.Write(-(value.Length + 1));
		writer.Write(Encoding.Unicode.GetBytes(value));
		writer.Write((ushort)0);
	}

	private static byte[] Compress(byte[] data)
	{
		using var output = new MemoryStream();
		using (var zlib = new ZlibStream(output, CompressionMode.Compress, leaveOpen: true))
			zlib.Write(data, 0, data.Length);
		return output.ToArray();
	}
}

/// <summary>A stable identity for a set of save files, comparable between disk and a manifest.</summary>
internal static class CloudSaveFingerprint
{
	public static string Compute(IEnumerable<(string Path, byte[] Sha1)> files)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		foreach (var (path, sha1) in files.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase))
		{
			hash.AppendData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
			hash.AppendData(new byte[] { 0 });
			hash.AppendData(sha1);
		}

		return Convert.ToHexString(hash.GetHashAndReset());
	}
}
