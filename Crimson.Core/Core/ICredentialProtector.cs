namespace Crimson.Core;

public interface ICredentialProtector
{
	string Protect(string value);
	string Unprotect(string protectedValue);
}
