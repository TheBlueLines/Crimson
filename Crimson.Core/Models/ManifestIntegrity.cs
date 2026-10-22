using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Crimson.Models;

public static class ManifestIntegrity
{
	public static void VerifyDigest(ReadOnlySpan<byte> data, string expectedDigest)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedDigest);
		var normalized = expectedDigest.Trim();
		var separator = normalized.IndexOf(':');
		if (separator >= 0)
			normalized = normalized[(separator + 1)..];

		byte[] expected;
		try
		{
			expected = Convert.FromHexString(normalized);
		}
		catch (FormatException exception)
		{
			throw new InvalidDataException("Manifest digest is not valid hexadecimal data.", exception);
		}

		var actual = expected.Length switch
		{
			20 => SHA1.HashData(data),
			32 => SHA256.HashData(data),
			_ => throw new InvalidDataException("Manifest digest algorithm is unsupported.")
		};
		if (!CryptographicOperations.FixedTimeEquals(actual, expected))
			throw new InvalidDataException("Manifest digest does not match the downloaded data.");
	}
}
