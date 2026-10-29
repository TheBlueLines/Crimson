using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using Crimson.Utils;
using Ionic.Zlib;

namespace Crimson.Models;

public sealed class Chunk
{
	private const uint HeaderMagic = 0xB1FE3AA2;
	private uint[] _guid;
	private byte[] _storedPayload = [];
	private byte[]? _data;
	private string? _guidStr;
	private BigInteger _guidNum = -1;
	private byte[]? _serialized;

	public Chunk()
	{
		_guid = GenerateGuid();
	}

	public uint HeaderVersion { get; private set; } = 3;
	public uint HeaderSize { get; private set; }
	public uint CompressedSize { get; private set; }
	public ulong Hash { get; private set; }
	public byte StoredAs { get; private set; }
	public byte HashType { get; private set; }
	public byte[] ShaHash { get; private set; } = new byte[20];
	public uint UncompressedSize { get; private set; } = 1024 * 1024;
	public bool Compressed => (StoredAs & 0x1) != 0;

	public string GuidStr => _guidStr ??= string.Join("-", _guid.Select(value => value.ToString("x8")));

	public BigInteger GuidNum
	{
		get
		{
			if (_guidNum == -1)
			{
				_guidNum = new BigInteger(_guid[3])
						   + (new BigInteger(_guid[2]) << 32)
						   + (new BigInteger(_guid[1]) << 64)
						   + (new BigInteger(_guid[0]) << 96);
			}

			return _guidNum;
		}
	}

	public byte[] Data
	{
		get
		{
			if (_data is not null)
				return _data;

			// Decompress only. The stored hashes are computed over the payload padded to a full
			// 1 MiB window (legendary chunk.py, the data setter), and which hashes are even
			// populated depends on HashType, so verifying the raw payload here rejects valid
			// chunks. legendary likewise does not verify on read.
			_data = Compressed
				? Decompress(_storedPayload, checked((int)UncompressedSize))
				: _storedPayload;

			return _data;
		}
	}

	public void ValidateAgainst(ChunkInfo expected)
	{
		ArgumentNullException.ThrowIfNull(expected);
		if (GuidNum != expected.GuidNum)
			throw new InvalidDataException("Chunk GUID does not match manifest metadata.");
		if (unchecked((long)Hash) != expected.Hash)
			throw new InvalidDataException("Chunk rolling hash header does not match manifest metadata.");
		if (expected.ShaHash.Length != 20 ||
			!CryptographicOperations.FixedTimeEquals(ShaHash, expected.ShaHash))
			throw new InvalidDataException("Chunk SHA-1 header does not match manifest metadata.");
		if (UncompressedSize != expected.WindowSize)
			throw new InvalidDataException("Chunk window size does not match manifest metadata.");
	}

	public static Chunk ReadBuffer(byte[] data)
	{
		ArgumentNullException.ThrowIfNull(data);
		if (data.Length > EpicProtocolLimits.MaximumChunkBytes + 4_096)
			throw new InvalidDataException("Chunk exceeds the supported size limit.");

		using var stream = new MemoryStream(data, writable: false);
		var reader = new EpicBinaryReader(stream);
		var chunk = Read(reader);
		reader.EnsureAtEnd("Chunk envelope");
		// The install pipeline only reads this array; copying it would add a full
		// chunk-sized allocation for every downloaded chunk. WriteToBuffer still
		// returns a defensive copy if serialization is ever needed.
		chunk._serialized = data;
		return chunk;
	}

	public static Chunk Read(BinaryReader reader)
	{
		ArgumentNullException.ThrowIfNull(reader);
		return Read(new EpicBinaryReader(reader.BaseStream));
	}

	internal static Chunk Read(EpicBinaryReader reader)
	{
		var headerStart = reader.Position;
		if (reader.ReadUInt32() != HeaderMagic)
			throw new InvalidDataException("Chunk header magic is invalid.");

		var chunk = new Chunk
		{
			HeaderVersion = reader.ReadUInt32(),
			HeaderSize = reader.ReadUInt32(),
			CompressedSize = reader.ReadUInt32(),
			_guid = [reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32()],
			Hash = reader.ReadUInt64(),
			StoredAs = reader.ReadByte()
		};
		if (chunk.HeaderVersion is < 1 or > 3)
			throw new InvalidDataException($"Chunk header version {chunk.HeaderVersion} is unsupported.");
		if ((chunk.StoredAs & ~0x1) != 0)
			throw new InvalidDataException("Chunk storage flags are unsupported.");
		if (chunk.CompressedSize > EpicProtocolLimits.MaximumChunkBytes)
			throw new InvalidDataException("Chunk compressed size exceeds the supported limit.");

		if (chunk.HeaderVersion >= 2)
		{
			chunk.ShaHash = reader.ReadBytesExact(20);
			chunk.HashType = reader.ReadByte();
			if (chunk.HashType > 3)
				throw new InvalidDataException($"Chunk hash type {chunk.HashType} is unsupported.");
		}
		if (chunk.HeaderVersion >= 3)
			chunk.UncompressedSize = reader.ReadUInt32();

		var actualHeaderSize = checked((uint)(reader.Position - headerStart));
		if (chunk.HeaderSize != actualHeaderSize)
			throw new InvalidDataException("Chunk header size does not match its version.");
		if (chunk.UncompressedSize > EpicProtocolLimits.MaximumChunkBytes)
			throw new InvalidDataException("Chunk uncompressed size exceeds the supported limit.");
		if (chunk.Compressed && chunk.UncompressedSize >
			Math.Max((long)chunk.CompressedSize, 1) * EpicProtocolLimits.MaximumChunkDecompressionRatio)
			throw new InvalidDataException("Chunk decompression ratio exceeds the supported limit.");
		if (reader.Remaining < chunk.CompressedSize)
			throw new EndOfStreamException("Chunk payload is truncated.");

		chunk._storedPayload = reader.ReadBytesExact(checked((int)chunk.CompressedSize));
		_ = chunk.Data;
		return chunk;
	}

	public byte[] WriteToBuffer() => _serialized?.ToArray()
		?? throw new InvalidOperationException("Only parsed chunks can be serialized.");

	private static byte[] Decompress(byte[] data, int expectedSize)
	{
		using var compressed = new MemoryStream(data, writable: false);
		using var zlib = new ZlibStream(compressed, CompressionMode.Decompress);
		var output = new byte[expectedSize];
		try
		{
			var total = 0;
			while (true)
			{
				var remaining = expectedSize - total;
				if (remaining == 0)
				{
					// Read one extra byte so an oversized payload is rejected rather than
					// silently truncated.
					Span<byte> extra = stackalloc byte[1];
					if (zlib.Read(extra) != 0)
						throw new InvalidDataException("Chunk decompressed beyond its declared size.");
					break;
				}

				var read = zlib.Read(output, total, remaining);
				if (read == 0)
					break;

				total = checked(total + read);
			}

			if (total != expectedSize)
				throw new InvalidDataException("Chunk decompressed size does not match its declaration.");

			return output;
		}
		catch (ZlibException exception)
		{
			throw new InvalidDataException("Chunk compressed payload is invalid.", exception);
		}
		catch (OverflowException exception)
		{
			throw new InvalidDataException("Chunk decompressed size overflowed.", exception);
		}
	}

	private static uint[] GenerateGuid()
	{
		var bytes = Guid.NewGuid().ToByteArray();
		return
		[
			BitConverter.ToUInt32(bytes, 0),
			BitConverter.ToUInt32(bytes, 4),
			BitConverter.ToUInt32(bytes, 8),
			BitConverter.ToUInt32(bytes, 12)
		];
	}
}
