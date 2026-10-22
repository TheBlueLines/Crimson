using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Crimson.Core;

public static class EpicEndpointPolicy
{
	private static readonly HashSet<string> ApiHosts = new(StringComparer.OrdinalIgnoreCase)
	{
		"account-public-service-prod03.ol.epicgames.com",
		"catalog-public-service-prod06.ol.epicgames.com",
		"ecommerceintegration-public-service-ecomprod02.ol.epicgames.com",
		"launcher-public-service-prod06.ol.epicgames.com"
	};

	private static readonly HashSet<string> LoginHosts = new(StringComparer.OrdinalIgnoreCase)
	{
		"epicgames.com",
		"www.epicgames.com",
		"accounts.epicgames.com"
	};

	private static readonly HashSet<string> StoreHosts = new(StringComparer.OrdinalIgnoreCase)
	{
		"launcher.store.epicgames.com",
		"store.epicgames.com"
	};

	private static readonly string[] ContentHostSuffixes =
	[
		".epicgames.com",
		".epicgamescdn.com",
		".akamaized.net",
		".cloudfront.net",
		// Epic serves egdownload.fastly-edge.com as a manifest base URL.
		".fastly-edge.com"
	];

	public static Uri RequireApiUri(string value) => Require(value, IsAllowedApiUri, "Epic API");

	public static Uri RequireContentUri(string value) => Require(value, IsAllowedContentUri, "Epic content");

	public static Uri RequireStoreUri(string value) => Require(value, IsAllowedStoreUri, "Epic Store");

	public static bool IsAllowedLoginOrigin(string? value) =>
		TryGetHttpsUri(value, out var uri) && LoginHosts.Contains(uri.Host);

	public static bool IsAllowedApiUri(Uri uri) => IsHttps(uri) && ApiHosts.Contains(uri.Host);

	public static bool IsAllowedStoreUri(Uri uri) => IsHttps(uri) && StoreHosts.Contains(uri.Host);

	public static bool IsAllowedContentUri(Uri uri) => IsHttps(uri) &&
		ContentHostSuffixes.Any(suffix => uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

	private static Uri Require(string value, Func<Uri, bool> predicate, string category)
	{
		if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !predicate(uri))
			throw new InvalidOperationException($"Unapproved {category} URI.");

		return uri;
	}

	private static bool TryGetHttpsUri(string? value, out Uri uri)
	{
		if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) && IsHttps(parsed))
		{
			uri = parsed;
			return true;
		}

		uri = null!;
		return false;
	}

	private static bool IsHttps(Uri uri) =>
		uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
		uri.Port == 443 &&
		string.IsNullOrEmpty(uri.UserInfo);
}

public static partial class SensitiveDataRedactor
{
	public const string Redacted = "[REDACTED]";

	public static string UriWithoutQuery(string value)
	{
		if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
			return Redacted;

		return uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
	}

	public static string Fields(string value) => SensitiveFieldPattern().Replace(
		value,
		match => $"{match.Groups[1].Value}{match.Groups[2].Value}{Redacted}");

	[GeneratedRegex(
		"(?i)(access_token|refresh_token|exchange_code|authorization|password|code)([\\\"']?\\s*[:=]\\s*[\\\"']?)([^&\\s\\\"',}]+)",
		RegexOptions.CultureInvariant)]
	private static partial Regex SensitiveFieldPattern();
}

public sealed class EpicLoginMessageGate
{
	public const int MaximumMessageLength = 4096;
	public const int MaximumCodeLength = 2048;

	private readonly HashSet<string> _acceptedCodeDigests = new(StringComparer.Ordinal);

	public bool TryAccept(string? source, string? message, out string exchangeCode)
	{
		exchangeCode = string.Empty;
		if (!EpicEndpointPolicy.IsAllowedLoginOrigin(source) ||
			string.IsNullOrEmpty(message) ||
			message.Length > MaximumMessageLength)
			return false;

		try
		{
			using var document = JsonDocument.Parse(message);
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 ||
				!root.TryGetProperty("type", out var type) || type.GetString() != "set_exchange_code" ||
				!root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String)
				return false;

			var value = code.GetString();
			if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumCodeLength ||
				value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
				return false;

			var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
			if (!_acceptedCodeDigests.Add(digest))
				return false;

			exchangeCode = value;
			return true;
		}
		catch (JsonException)
		{
			return false;
		}
	}
}
