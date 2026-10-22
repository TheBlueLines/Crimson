using System.Security.Cryptography;
using Crimson.Utils;

namespace Crimson.Tests;

public sealed class CredentialProtectorTests
{
	[Fact]
	public void WindowsProtectorRoundTripsWithoutPlaintext()
	{
		const string value = "credential-canary-48b1";
		var protector = new WindowsCredentialProtector();

		var protectedValue = protector.Protect(value);

		Assert.DoesNotContain(value, protectedValue, StringComparison.Ordinal);
		Assert.Equal(value, protector.Unprotect(protectedValue));
		Assert.Equal(value, KeyManager.DecryptString(protectedValue));
	}

	[Fact]
	public void WindowsProtectorRejectsMalformedPayload()
	{
		var protector = new WindowsCredentialProtector();

		Assert.Throws<CryptographicException>(() => protector.Unprotect("invalid"));
	}
}
