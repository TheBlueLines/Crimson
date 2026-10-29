using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace Crimson.Utils;

internal static class FileHash
{
	public static string ComputeSha1(string filePath)
	{
		using var sha1 = SHA1.Create();
		using var stream = File.OpenRead(filePath);
		return Convert.ToHexString(sha1.ComputeHash(stream)).ToLowerInvariant();
	}

	public static bool Sha1Matches(string filePath, ReadOnlySpan<byte> expected)
	{
		using var sha1 = SHA1.Create();
		using var stream = File.OpenRead(filePath);
		var actual = sha1.ComputeHash(stream);
		return CryptographicOperations.FixedTimeEquals(actual, expected);
	}

	/// <summary>
	/// Same as <see cref="Sha1Matches(string, ReadOnlySpan{byte})"/>, but stops between reads when
	/// <paramref name="cancellationToken"/> is cancelled so hashing a large file can be abandoned.
	/// </summary>
	public static bool Sha1Matches(string filePath, ReadOnlySpan<byte> expected, CancellationToken cancellationToken)
	{
		using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
		using var stream = File.OpenRead(filePath);
		var buffer = new byte[1024 * 1024];
		int read;
		while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			sha1.AppendData(buffer, 0, read);
		}
		return CryptographicOperations.FixedTimeEquals(sha1.GetHashAndReset(), expected);
	}
}
