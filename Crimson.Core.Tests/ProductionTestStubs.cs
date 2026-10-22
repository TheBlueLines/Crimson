using System.Text;

namespace Crimson.Tests
{
	internal sealed class TestCredentialProtector : Crimson.Core.ICredentialProtector
	{
		private const string Prefix = "test-protected:";

		public string Protect(string value) =>
			Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

		public string Unprotect(string protectedValue)
		{
			if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
				return protectedValue;

			return Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[Prefix.Length..]));
		}
	}
}
