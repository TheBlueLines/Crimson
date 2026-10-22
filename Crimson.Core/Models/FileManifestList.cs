using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Crimson.Models;

public sealed class FileManifestList
{
	public int Version { get; internal set; }
	public int Size { get; internal set; }
	public int Count { get; internal set; }
	public List<FileManifest> Elements { get; } = [];

	private Dictionary<string, int>? _pathMap;

	public FileManifest GetFileByPath(string path)
	{
		_pathMap ??= Elements
			.Select((element, index) => (element.Filename, index))
			.ToDictionary(item => item.Filename, item => item.index, StringComparer.Ordinal);
		return _pathMap.TryGetValue(path, out var index)
			? Elements[index]
			: throw new ArgumentException($"Invalid manifest path: {path}", nameof(path));
	}

	public static FileManifestList Read(Stream stream) => Read(new EpicBinaryReader(stream));

	internal static FileManifestList Read(EpicBinaryReader reader)
	{
		var start = reader.Position;
		var end = reader.BeginSection("File manifest list");
		var list = new FileManifestList
		{
			Size = checked((int)(end - start)),
			Version = reader.ReadByte(),
			Count = reader.ReadCount(EpicProtocolLimits.MaximumFileCount, "File")
		};
		if (list.Version is < 0 or > 2)
			throw new InvalidDataException($"File manifest list version {list.Version} is unsupported.");

		// No case-insensitive uniqueness check: Epic builds from case-sensitive trees, so a
		// Readme.txt/README.txt pair is legal and must not make a title uninstallable.
		// ReadUnrealString (not ReadUtf8String) because a negative length signals UTF-16.
		for (var index = 0; index < list.Count; index++)
		{
			var filename = reader.ReadUnrealString(EpicProtocolLimits.MaximumPathBytes);
			if (string.IsNullOrWhiteSpace(filename))
				throw new InvalidDataException("Manifest filename is empty.");

			list.Elements.Add(new FileManifest { Filename = filename });
		}

		foreach (var file in list.Elements)
			file.SymlinkTarget = reader.ReadUnrealString(EpicProtocolLimits.MaximumPathBytes);
		foreach (var file in list.Elements)
			file.Hash = reader.ReadBytesExact(20);
		foreach (var file in list.Elements)
		{
			// Unknown flag bits are ignored, not rejected; only bits 0-2 are meaningful.
			file.Flags = reader.ReadByte();
		}

		foreach (var file in list.Elements)
		{
			var tagCount = reader.ReadCount(EpicProtocolLimits.MaximumTagsPerFile, "Install tag");
			for (var index = 0; index < tagCount; index++)
				file.InstallTags.Add(reader.ReadUnrealString());
		}

		long cumulativePartCount = 0;
		foreach (var file in list.Elements)
		{
			var partCount = reader.ReadCount(EpicProtocolLimits.MaximumChunkPartsPerFile, "Chunk part");
			cumulativePartCount = checked(cumulativePartCount + partCount);
			if (cumulativePartCount > EpicProtocolLimits.MaximumCumulativeChunkParts)
				throw new InvalidDataException("Cumulative chunk part count exceeds the supported limit.");

			long fileOffset = 0;
			for (var index = 0; index < partCount; index++)
			{
				var partStart = reader.Position;
				var partSize = reader.ReadInt32();
				if (partSize < 28)
					throw new InvalidDataException("Chunk part section is too small.");

				var partEnd = checked(partStart + partSize);
				if (partEnd > end)
					throw new InvalidDataException("Chunk part extends beyond the file manifest section.");
				var part = new ChunkPart
				{
					Guid = [reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()],
					Offset = reader.ReadInt32(),
					Size = reader.ReadInt32(),
					FileOffset = fileOffset
				};
				if (part.Offset < 0 || part.Size < 0 ||
					(long)part.Offset + part.Size > EpicProtocolLimits.MaximumChunkBytes)
					throw new InvalidDataException("Chunk part range is outside the supported chunk window.");

				fileOffset = checked(fileOffset + part.Size);
				file.ChunkParts.Add(part);
				reader.SkipTo(partEnd, "Chunk part");
			}

			file.FileSize = fileOffset;
		}

		if (list.Version >= 1)
		{
			foreach (var file in list.Elements)
			{
				var hasMd5 = reader.ReadInt32();
				if (hasMd5 is not (0 or 1))
					throw new InvalidDataException("File MD5 presence flag is invalid.");
				if (hasMd5 == 1)
					file.HashMd5 = reader.ReadBytesExact(16);
			}

			foreach (var file in list.Elements)
				file.MimeType = reader.ReadUnrealString();
		}

		if (list.Version >= 2)
		{
			foreach (var file in list.Elements)
				file.HashSha256 = reader.ReadBytesExact(32);
		}

		if (!reader.EndSection(end, "File manifest list"))
			list.Version = 0;
		return list;
	}
}

public sealed class FileManifest
{
	public string Filename { get; set; } = string.Empty;
	public string SymlinkTarget { get; set; } = string.Empty;
	public byte[] Hash { get; set; } = [];
	public byte Flags { get; set; }
	public List<string> InstallTags { get; } = [];
	public List<ChunkPart> ChunkParts { get; } = [];
	public long FileSize { get; set; }
	public byte[] HashMd5 { get; set; } = [];
	public string MimeType { get; set; } = string.Empty;
	public byte[] HashSha256 { get; set; } = [];

	public bool ReadOnly => (Flags & 0x1) != 0;
	public bool Compressed => (Flags & 0x2) != 0;
	public bool Executable => (Flags & 0x4) != 0;
	public byte[] ShaHash => Hash;

	public override string ToString()
	{
		var chunkParts = ChunkParts.Count <= 20
			? string.Join(", ", ChunkParts)
			: $"{string.Join(", ", ChunkParts.Take(20))}, [...]";
		return $"<FileManifest (filename=\"{Filename}\", symlink_target=\"{SymlinkTarget}\", hash={Convert.ToHexString(Hash)}, flags={Flags}, install_tags=[{string.Join(", ", InstallTags)}], chunk_parts=[{chunkParts}], file_size={FileSize})>";
	}
}

public sealed class ChunkPart
{
	public int[] Guid { get; set; }
	public int Offset { get; set; }
	public int Size { get; set; }
	public long FileOffset { get; set; }

	private string? _guidStr;
	private BigInteger _guidNum = -1;

	public ChunkPart(int[]? guid = null, int offset = 0, int size = 0, int fileOffset = 0)
	{
		Guid = guid ?? new int[4];
		Offset = offset;
		Size = size;
		FileOffset = fileOffset;
	}

	public string GuidStr => _guidStr ??= string.Join("-", Guid.Select(value => value.ToString("x8")));

	public BigInteger GuidNum
	{
		get
		{
			if (_guidNum == -1)
			{
				if (Guid.Length != 4)
					throw new InvalidOperationException("Chunk part GUID is not initialized.");

				_guidNum = new BigInteger(unchecked((uint)Guid[3]))
						   + (new BigInteger(unchecked((uint)Guid[2])) << 32)
						   + (new BigInteger(unchecked((uint)Guid[1])) << 64)
						   + (new BigInteger(unchecked((uint)Guid[0])) << 96);
			}

			return _guidNum;
		}
	}

	public override string ToString() =>
		$"<ChunkPart (guid={GuidStr}, offset={Offset}, size={Size}, file_offset={FileOffset})>";
}
