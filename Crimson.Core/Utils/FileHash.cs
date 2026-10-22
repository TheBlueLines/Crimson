using System;
using System.IO;
using System.Security.Cryptography;

namespace Crimson.Utils;

internal static class FileHash
{
	public static string ComputeSha1(string filePath)
	{
		using var sha1 = SHA1.Create();
		using var stream = File.OpenRead(filePath);
		return Convert.ToHexString(sha1.ComputeHash(stream)).ToLowerInvariant();
	}
}
