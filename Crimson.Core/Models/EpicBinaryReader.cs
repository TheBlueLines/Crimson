using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Crimson.Models;

internal static class EpicProtocolLimits
{
	public const int MaximumManifestBytes = 512 * 1024 * 1024;
	public const int MaximumChunkBytes = 64 * 1024 * 1024;
	public const int MaximumStringBytes = 1024 * 1024;
	public const int MaximumPathBytes = 32 * 1024;
	public const int MaximumFileCount = 1_000_000;
	public const int MaximumChunkCount = 1_000_000;
	public const int MaximumTagsPerFile = 4_096;
	public const int MaximumChunkPartsPerFile = 1_000_000;
	public const long MaximumCumulativeChunkParts = 10_000_000;
	public const int MaximumCustomFields = 100_000;
	public const int MaximumManifestDecompressionRatio = 100;
	public const int MaximumChunkDecompressionRatio = 4_096;
}

internal sealed class EpicBinaryReader
{
	private static readonly UTF8Encoding StrictUtf8 = new(false, true);
	private static readonly UnicodeEncoding StrictUtf16 = new(false, false, true);
	private readonly BinaryReader _reader;
	private readonly long _limit;

	public EpicBinaryReader(Stream stream, long? limit = null)
	{
		ArgumentNullException.ThrowIfNull(stream);
		if (!stream.CanRead || !stream.CanSeek)
			throw new InvalidDataException("Epic binary input must be readable and seekable.");

		_limit = limit ?? stream.Length;
		if (_limit < stream.Position || _limit > stream.Length)
			throw new InvalidDataException("Epic binary input has an invalid boundary.");

		_reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
	}

	public long Position => _reader.BaseStream.Position;

	public long Remaining => _limit - Position;

	public byte ReadByte() => ReadPrimitive(1, _reader.ReadByte);

	public int ReadInt32() => ReadPrimitive(sizeof(int), _reader.ReadInt32);

	public uint ReadUInt32() => ReadPrimitive(sizeof(uint), _reader.ReadUInt32);

	public long ReadInt64() => ReadPrimitive(sizeof(long), _reader.ReadInt64);

	public ulong ReadUInt64() => ReadPrimitive(sizeof(ulong), _reader.ReadUInt64);

	public byte[] ReadBytesExact(int count)
	{
		if (count < 0)
			throw new InvalidDataException("Negative binary read length.");

		EnsureRemaining(count);
		var bytes = _reader.ReadBytes(count);
		if (bytes.Length != count)
			throw new EndOfStreamException("Epic binary input ended during an exact read.");

		return bytes;
	}

	public int ReadCount(int maximum, string name)
	{
		var count = ReadInt32();
		if (count < 0 || count > maximum)
			throw new InvalidDataException($"{name} count {count} is outside the supported range.");

		return count;
	}

	public string ReadUtf8String(int maximumBytes = EpicProtocolLimits.MaximumStringBytes)
	{
		var byteCount = ReadInt32();
		if (byteCount == 0)
			return string.Empty;
		if (byteCount < 0 || byteCount > maximumBytes)
			throw new InvalidDataException("UTF-8 string length is outside the supported range.");

		var bytes = ReadBytesExact(byteCount);
		if (bytes[^1] != 0)
			throw new InvalidDataException("UTF-8 string is missing its null terminator.");

		try
		{
			return StrictUtf8.GetString(bytes, 0, bytes.Length - 1);
		}
		catch (DecoderFallbackException exception)
		{
			throw new InvalidDataException("UTF-8 string contains invalid data.", exception);
		}
	}

	public string ReadUnrealString(int maximumBytes = EpicProtocolLimits.MaximumStringBytes)
	{
		var length = ReadInt32();
		if (length == 0)
			return string.Empty;

		try
		{
			if (length > 0)
			{
				if (length > maximumBytes)
					throw new InvalidDataException("String length is outside the supported range.");

				var bytes = ReadBytesExact(length);
				if (bytes[^1] != 0)
					throw new InvalidDataException("String is missing its null terminator.");

				return StrictUtf8.GetString(bytes, 0, bytes.Length - 1);
			}

			var characterCount = checked(-length);
			var byteCount = checked(characterCount * 2);
			if (byteCount > maximumBytes)
				throw new InvalidDataException("UTF-16 string length is outside the supported range.");

			var utf16Bytes = ReadBytesExact(byteCount);
			if (utf16Bytes[^2] != 0 || utf16Bytes[^1] != 0)
				throw new InvalidDataException("UTF-16 string is missing its null terminator.");

			return StrictUtf16.GetString(utf16Bytes, 0, utf16Bytes.Length - 2);
		}
		catch (OverflowException exception)
		{
			throw new InvalidDataException("String length overflowed.", exception);
		}
		catch (DecoderFallbackException exception)
		{
			throw new InvalidDataException("String contains invalid data.", exception);
		}
	}

	public long BeginSection(string name, int minimumSize = 9)
	{
		var start = Position;
		var size = ReadInt32();
		if (size < minimumSize)
			throw new InvalidDataException($"{name} section is too small.");

		long end;
		try
		{
			end = checked(start + size);
		}
		catch (OverflowException exception)
		{
			throw new InvalidDataException($"{name} section size overflowed.", exception);
		}

		if (end > _limit)
			throw new EndOfStreamException($"{name} section extends beyond its containing data.");

		return end;
	}

	/// <summary>
	/// Seeks to the declared end of a section, tolerating a short read.
	/// </summary>
	/// <remarks>
	/// Epic adds fields to the tail of a section when it bumps a feature level, so a newer
	/// manifest legitimately carries bytes this parser does not understand. legendary skips
	/// forward and downgrades the version rather than failing (models/manifest.py, the
	/// "Did not read entire ..." warnings). Overrunning a section is still an error.
	/// </remarks>
	/// <returns><c>true</c> when the section was consumed exactly.</returns>
	public bool EndSection(long expectedEnd, string name)
	{
		if (Position == expectedEnd)
			return true;
		if (Position > expectedEnd)
			throw new InvalidDataException($"{name} section overran its declared size.");

		_reader.BaseStream.Position = expectedEnd;
		return false;
	}

	public void SkipTo(long position, string name)
	{
		if (position < Position || position > _limit)
			throw new InvalidDataException($"{name} boundary is invalid.");

		_reader.BaseStream.Position = position;
	}

	/// <summary>
	/// Requires the input to be fully consumed. Used for fixed-size envelopes, where trailing
	/// bytes indicate corruption rather than a newer feature level.
	/// </summary>
	public void EnsureAtEnd(string name)
	{
		if (Position != _limit)
			throw new InvalidDataException($"{name} contains trailing data.");
	}

	private T ReadPrimitive<T>(int size, Func<T> read)
	{
		EnsureRemaining(size);
		return read();
	}

	private void EnsureRemaining(long count)
	{
		if (count < 0 || count > Remaining)
			throw new EndOfStreamException("Epic binary input ended during a bounded read.");
	}
}
