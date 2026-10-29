using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Crimson.Models;

internal static class JsonManifestReader
{
	public static bool IsJson(ReadOnlySpan<byte> data)
	{
		foreach (var value in data)
		{
			if (!char.IsWhiteSpace((char)value))
				return value == (byte)'{';
		}

		return false;
	}

	public static Manifest Read(byte[] data)
	{
		try
		{
			return ReadCore(data);
		}
		catch (InvalidDataException)
		{
			throw;
		}
		catch (Exception exception) when (exception is JsonException or FormatException or OverflowException or KeyNotFoundException or ArgumentException)
		{
			throw new InvalidDataException("JSON manifest is malformed.", exception);
		}
	}

	private static Manifest ReadCore(byte[] data)
	{
		if (data.Length > EpicProtocolLimits.MaximumManifestBytes)
			throw new InvalidDataException("JSON manifest exceeds the supported size limit.");

		using var document = JsonDocument.Parse(data, new JsonDocumentOptions
		{
			MaxDepth = 64,
			CommentHandling = JsonCommentHandling.Disallow,
			AllowTrailingCommas = false
		});
		var root = document.RootElement;
		// Every field here is optional in legendary (json_data.pop with a default). JSON
		// manifests are served for the oldest titles, which routinely omit launch/prereq keys.
		var manifestVersion = DecodeInt32(GetOptionalString(root, "ManifestFileVersion") ?? "013000000000");
		if (manifestVersion is < 1 or > 24)
			throw new InvalidDataException($"JSON manifest version {manifestVersion} is unsupported.");

		var meta = new ManifestMeta
		{
			FeatureLevel = checked((uint)manifestVersion),
			IsFileData = GetOptionalBoolean(root, "bIsFileData"),
			AppId = unchecked((uint)DecodeInt32(GetOptionalString(root, "AppID") ?? "000000000000")),
			AppName = GetOptionalString(root, "AppNameString") ?? string.Empty,
			BuildVersion = GetOptionalString(root, "BuildVersionString") ?? string.Empty,
			LaunchExe = GetOptionalString(root, "LaunchExeString") ?? string.Empty,
			LaunchCommand = GetOptionalString(root, "LaunchCommand") ?? string.Empty,
			PrereqName = GetOptionalString(root, "PrereqName") ?? string.Empty,
			PrereqPath = GetOptionalString(root, "PrereqPath") ?? string.Empty,
			PrereqArgs = GetOptionalString(root, "PrereqArgs") ?? string.Empty
		};
		if (root.TryGetProperty("PrereqIds", out var prereqIds))
		{
			if (prereqIds.ValueKind != JsonValueKind.Array ||
				prereqIds.GetArrayLength() > 4_096)
				throw new InvalidDataException("JSON manifest prerequisites are invalid.");
			meta.PrereqIds = prereqIds.EnumerateArray()
				.Select(element => GetBoundedString(element, "PrereqIds"))
				.ToList();
		}

		var chunkHashes = GetRequiredObject(root, "ChunkHashList");
		var chunkSha = GetRequiredObject(root, "ChunkShaList");
		var chunkGroups = GetRequiredObject(root, "DataGroupList");
		var chunkFileSizes = GetRequiredObject(root, "ChunkFilesizeList");

		var cdl = new CDL(manifestVersion) { Version = 0 };
		foreach (var hashProperty in chunkHashes.EnumerateObject())
		{
			if (cdl.Elements.Count >= EpicProtocolLimits.MaximumChunkCount)
				throw new InvalidDataException("JSON manifest chunk count exceeds the supported limit.");

			var guid = ParseGuid(hashProperty.Name);
			var guidText = hashProperty.Name;
			if (!chunkSha.TryGetProperty(guidText, out var shaElement) ||
				!chunkGroups.TryGetProperty(guidText, out var groupElement) ||
				!chunkFileSizes.TryGetProperty(guidText, out var sizeElement))
				throw new InvalidDataException($"JSON manifest chunk metadata is incomplete: {guidText}.");

			var sha = ParseHexBytes(GetBoundedString(shaElement, "ChunkShaList"), 20);
			var group = DecodeByteString(GetBoundedString(groupElement, "DataGroupList"));
			var fileSize = DecodeInt64(GetBoundedString(sizeElement, "ChunkFilesizeList"));
			if (group > 99 || fileSize < 0 || fileSize > EpicProtocolLimits.MaximumChunkBytes + 4_096L)
				throw new InvalidDataException($"JSON manifest chunk metadata is out of range: {guidText}.");

			cdl.Elements.Add(new ChunkInfo(manifestVersion)
			{
				Guid = guid,
				Hash = DecodeInt64(GetBoundedString(hashProperty.Value, "ChunkHashList")),
				ShaHash = sha,
				GroupNum = group,
				WindowSize = 1024 * 1024,
				FileSize = fileSize
			});
		}
		cdl.Count = cdl.Elements.Count;

		var filesElement = root.GetProperty("FileManifestList");
		if (filesElement.ValueKind != JsonValueKind.Array ||
			filesElement.GetArrayLength() > EpicProtocolLimits.MaximumFileCount)
			throw new InvalidDataException("JSON file manifest list is invalid.");
		var files = new FileManifestList { Version = 0 };
		long cumulativeParts = 0;
		foreach (var fileElement in filesElement.EnumerateArray())
		{
			// No case-insensitive uniqueness check, matching the binary reader.
			var filename = GetOptionalString(fileElement, "Filename") ?? string.Empty;
			byte flags = 0;
			if (GetOptionalBoolean(fileElement, "bIsReadOnly")) flags |= 0x1;
			if (GetOptionalBoolean(fileElement, "bIsCompressed")) flags |= 0x2;
			if (GetOptionalBoolean(fileElement, "bIsUnixExecutable")) flags |= 0x4;
			var file = new FileManifest
			{
				Filename = filename,
				Hash = DecodeDecimalBytes(GetRequiredString(fileElement, "FileHash"), 20),
				Flags = flags
			};
			if (fileElement.TryGetProperty("InstallTags", out var installTags) &&
				installTags.ValueKind == JsonValueKind.Array)
			{
				foreach (var tag in installTags.EnumerateArray())
				{
					if (tag.ValueKind == JsonValueKind.String)
						file.InstallTags.Add(tag.GetString() ?? string.Empty);
				}
			}
			if (!fileElement.TryGetProperty("FileChunkParts", out var parts) ||
				parts.ValueKind != JsonValueKind.Array ||
				parts.GetArrayLength() > EpicProtocolLimits.MaximumChunkPartsPerFile)
				throw new InvalidDataException($"JSON chunk parts are invalid for {filename}.");

			long fileOffset = 0;
			foreach (var partElement in parts.EnumerateArray())
			{
				cumulativeParts++;
				if (cumulativeParts > EpicProtocolLimits.MaximumCumulativeChunkParts)
					throw new InvalidDataException("JSON cumulative chunk part count exceeds the supported limit.");
				var offset = DecodeInt32(GetRequiredString(partElement, "Offset"));
				var size = DecodeInt32(GetRequiredString(partElement, "Size"));
				if (offset < 0 || size < 0 || (long)offset + size > EpicProtocolLimits.MaximumChunkBytes)
					throw new InvalidDataException($"JSON chunk part range is invalid for {filename}.");

				var part = new ChunkPart(ParseGuid(GetRequiredString(partElement, "Guid")), offset, size)
				{
					FileOffset = fileOffset
				};
				_ = cdl.GetChunkByGuidNum(part.GuidNum);
				file.ChunkParts.Add(part);
				fileOffset = checked(fileOffset + size);
			}

			file.FileSize = fileOffset;
			files.Elements.Add(file);
		}
		files.Count = files.Elements.Count;

		var customFields = new CustomFields { Version = 0 };
		if (root.TryGetProperty("CustomFields", out var customElement))
		{
			if (customElement.ValueKind != JsonValueKind.Object)
				throw new InvalidDataException("JSON custom fields are invalid.");

			var fieldCount = 0;
			foreach (var property in customElement.EnumerateObject())
			{
				if (++fieldCount > EpicProtocolLimits.MaximumCustomFields)
					throw new InvalidDataException("JSON custom fields are invalid.");
				customFields[property.Name] = GetBoundedString(property.Value, "CustomFields");
			}
			customFields.Count = fieldCount;
		}

		return new Manifest
		{
			ManifestMeta = meta,
			CDL = cdl,
			FileManifestList = files,
			CustomFields = customFields
		};
	}

	private static JsonElement GetRequiredObject(JsonElement root, string name)
	{
		if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
			throw new InvalidDataException($"JSON manifest field {name} is missing or invalid.");
		return value;
	}

	private static string? GetOptionalString(JsonElement root, string name) =>
		root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	private static bool GetOptionalBoolean(JsonElement root, string name) =>
		root.TryGetProperty(name, out var value) &&
		value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
		value.GetBoolean();

	private static string GetRequiredString(JsonElement root, string name)
	{
		if (!root.TryGetProperty(name, out var value))
			throw new InvalidDataException($"JSON manifest field {name} is missing.");
		return GetBoundedString(value, name);
	}

	private static bool GetRequiredBoolean(JsonElement root, string name)
	{
		if (!root.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
			throw new InvalidDataException($"JSON manifest field {name} is missing or invalid.");
		return value.GetBoolean();
	}

	private static string GetBoundedString(JsonElement value, string name)
	{
		if (value.ValueKind != JsonValueKind.String)
			throw new InvalidDataException($"JSON manifest field {name} is not a string.");
		var text = value.GetString() ?? string.Empty;
		if (Encoding.UTF8.GetByteCount(text) > EpicProtocolLimits.MaximumStringBytes)
			throw new InvalidDataException($"JSON manifest field {name} exceeds the supported length.");
		return text;
	}

	private static int[] ParseGuid(string value)
	{
		if (value.Length != 32)
			throw new InvalidDataException("JSON chunk GUID length is invalid.");

		var guid = new int[4];
		try
		{
			for (var index = 0; index < guid.Length; index++)
			{
				guid[index] = unchecked((int)uint.Parse(
					value.AsSpan(index * 8, 8),
					NumberStyles.HexNumber,
					CultureInfo.InvariantCulture));
			}
			return guid;
		}
		catch (FormatException exception)
		{
			throw new InvalidDataException("JSON chunk GUID is invalid.", exception);
		}
	}

	private static byte[] ParseHexBytes(string value, int expectedLength)
	{
		try
		{
			var bytes = Convert.FromHexString(value);
			return bytes.Length == expectedLength
				? bytes
				: throw new InvalidDataException("JSON hash length is invalid.");
		}
		catch (FormatException exception)
		{
			throw new InvalidDataException("JSON hash is invalid.", exception);
		}
	}

	private static byte[] DecodeDecimalBytes(string value, int expectedLength)
	{
		if (value.Length != expectedLength * 3)
			throw new InvalidDataException("JSON byte string length is invalid.");
		var bytes = new byte[expectedLength];
		for (var index = 0; index < expectedLength; index++)
		{
			if (!byte.TryParse(value.AsSpan(index * 3, 3), NumberStyles.None, CultureInfo.InvariantCulture, out bytes[index]))
				throw new InvalidDataException("JSON byte string is invalid.");
		}
		return bytes;
	}

	private static byte DecodeByteString(string value)
	{
		if (value.Length != 3)
			throw new InvalidDataException("JSON byte string length is invalid.");
		if (!byte.TryParse(value.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var result))
			throw new InvalidDataException("JSON byte string is invalid.");
		return result;
	}

	private static int DecodeInt32(ReadOnlySpan<char> value)
	{
		if (value.Length != 4 * 3)
			throw new InvalidDataException("JSON byte string length is invalid.");

		var result = 0;
		for (var index = 0; index < 4; index++)
		{
			if (!byte.TryParse(value.Slice(index * 3, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var part))
				throw new InvalidDataException("JSON byte string is invalid.");
			result |= part << (index * 8);
		}
		return result;
	}

	private static long DecodeInt64(ReadOnlySpan<char> value)
	{
		if (value.Length != 8 * 3)
			throw new InvalidDataException("JSON byte string length is invalid.");

		var result = 0L;
		for (var index = 0; index < 8; index++)
		{
			if (!byte.TryParse(value.Slice(index * 3, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var part))
				throw new InvalidDataException("JSON byte string is invalid.");
			result |= (long)part << (index * 8);
		}
		return result;
	}
}
