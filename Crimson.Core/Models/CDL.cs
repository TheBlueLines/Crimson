using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Crimson.Models;

public sealed class CDL
{
	public int Version { get; internal set; }
	public int Size { get; internal set; }
	public int Count { get; internal set; }
	public List<ChunkInfo> Elements { get; } = [];

	private readonly int _manifestVersion;
	private Dictionary<string, int>? _guidMap;
	private Dictionary<BigInteger, int>? _guidIntMap;
	private Dictionary<string, int>? _pathMap;

	public CDL(int manifestVersion = 18)
	{
		_manifestVersion = manifestVersion;
	}

	public ChunkInfo GetChunkByPath(string path)
	{
		_pathMap ??= Elements
			.Select((element, index) => (element.Path, index))
			.ToDictionary(item => item.Path, item => item.index, StringComparer.Ordinal);
		return _pathMap.TryGetValue(path, out var index)
			? Elements[index]
			: throw new ArgumentException($"Invalid chunk path: {path}", nameof(path));
	}

	public ChunkInfo GetChunkByGuid(object guid) => guid switch
	{
		int integer => GetChunkByGuidNum(new BigInteger(integer)),
		BigInteger integer => GetChunkByGuidNum(integer),
		string text => GetChunkByGuidStr(text),
		_ => throw new ArgumentException("Invalid chunk GUID type.", nameof(guid))
	};

	public ChunkInfo GetChunkByGuidStr(string guid)
	{
		_guidMap ??= Elements
			.Select((element, index) => (Guid: element.GuidStr, index))
			.ToDictionary(item => item.Guid, item => item.index, StringComparer.OrdinalIgnoreCase);
		return _guidMap.TryGetValue(guid, out var index)
			? Elements[index]
			: throw new ArgumentException($"Invalid chunk GUID: {guid}", nameof(guid));
	}

	public ChunkInfo GetChunkByGuidNum(BigInteger guid)
	{
		_guidIntMap ??= Elements
			.Select((element, index) => (element.GuidNum, index))
			.ToDictionary(item => item.GuidNum, item => item.index);
		return _guidIntMap.TryGetValue(guid, out var index)
			? Elements[index]
			: throw new ArgumentException($"Invalid chunk GUID: {guid:x}", nameof(guid));
	}

	public static CDL Read(Stream stream, int manifestVersion = 18) =>
		Read(new EpicBinaryReader(stream), manifestVersion);

	internal static CDL Read(EpicBinaryReader reader, int manifestVersion = 18)
	{
		var start = reader.Position;
		var end = reader.BeginSection("Chunk data list");
		var cdl = new CDL(manifestVersion)
		{
			Size = checked((int)(end - start)),
			Version = reader.ReadByte(),
			Count = reader.ReadCount(EpicProtocolLimits.MaximumChunkCount, "Chunk")
		};

		for (var index = 0; index < cdl.Count; index++)
			cdl.Elements.Add(new ChunkInfo(manifestVersion));

		foreach (var chunk in cdl.Elements)
			chunk.Guid = [reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()];
		foreach (var chunk in cdl.Elements)
			chunk.Hash = reader.ReadInt64();
		foreach (var chunk in cdl.Elements)
			chunk.ShaHash = reader.ReadBytesExact(20);
		foreach (var chunk in cdl.Elements)
		{
			chunk.GroupNum = reader.ReadByte();
			if (chunk.GroupNum is < 0 or > 99)
				throw new InvalidDataException("Chunk group number is outside the supported range.");
		}
		foreach (var chunk in cdl.Elements)
		{
			chunk.WindowSize = reader.ReadInt32();
			if (chunk.WindowSize < 0 || chunk.WindowSize > EpicProtocolLimits.MaximumChunkBytes)
				throw new InvalidDataException("Chunk window size is outside the supported range.");
		}
		foreach (var chunk in cdl.Elements)
		{
			chunk.FileSize = reader.ReadInt64();
			if (chunk.FileSize < 0 || chunk.FileSize > EpicProtocolLimits.MaximumChunkBytes + 4_096L)
				throw new InvalidDataException("Chunk file size is outside the supported range.");
		}

		if (!reader.EndSection(end, "Chunk data list"))
			cdl.Version = 0;
		return cdl;
	}
}

public sealed class ChunkInfo
{
	public int[] Guid { get; set; } = [];
	public long Hash { get; set; }
	public byte[] ShaHash { get; set; } = [];
	public int WindowSize { get; set; }
	public long FileSize { get; set; }

	private readonly int _manifestVersion;
	private int? _groupNum;
	private string? _guidStr;
	private BigInteger _guidNum = -1;

	public ChunkInfo(int manifestVersion = 18)
	{
		_manifestVersion = manifestVersion;
	}

	public string GuidStr => _guidStr ??= string.Join("-", Guid.Select(value => value.ToString("x8")));

	public BigInteger GuidNum
	{
		get
		{
			if (_guidNum == -1)
			{
				if (Guid.Length != 4)
					throw new InvalidOperationException("Chunk GUID is not initialized.");

				_guidNum = new BigInteger(unchecked((uint)Guid[3]))
						   + (new BigInteger(unchecked((uint)Guid[2])) << 32)
						   + (new BigInteger(unchecked((uint)Guid[1])) << 64)
						   + (new BigInteger(unchecked((uint)Guid[0])) << 96);
			}

			return _guidNum;
		}
	}

	public int GroupNum
	{
		get
		{
			if (!_groupNum.HasValue)
			{
				if (Guid.Length != 4)
					throw new InvalidOperationException("Chunk GUID is not initialized.");

				var guidBytes = Guid.SelectMany(BitConverter.GetBytes).ToArray();
				_groupNum = (int)(Crc32.Compute(guidBytes) % 100);
			}

			return _groupNum.Value;
		}
		set => _groupNum = value;
	}

	public string Path
	{
		get
		{
			var guidHex = string.Join(string.Empty, Guid.Select(value => value.ToString("X8")));
			return $"{GetChunkDirectory(_manifestVersion)}/{GroupNum:D2}/{Hash:X16}_{guidHex}.chunk";
		}
	}

	public override string ToString() =>
		$"<ChunkInfo (guid={GuidStr}, hash={Hash}, sha_hash={Convert.ToHexString(ShaHash)}, group_num={GroupNum}, window_size={WindowSize}, file_size={FileSize})>";

	private static string GetChunkDirectory(int manifestVersion) => manifestVersion switch
	{
		>= 15 => "ChunksV4",
		>= 6 => "ChunksV3",
		>= 3 => "ChunksV2",
		_ => "Chunks"
	};

	private static class Crc32
	{
		public static uint Compute(byte[] bytes)
		{
			var crc = 0xFFFFFFFFu;
			foreach (var value in bytes)
			{
				crc ^= value;
				for (var bit = 0; bit < 8; bit++)
					crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
			}

			return ~crc;
		}
	}
}
