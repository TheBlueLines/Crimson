using System.Text.Json.Serialization;

namespace Crimson.Models;

public class UserData
{
	[JsonPropertyName("access_token")]
	public string AccessToken { get; set; }

	[JsonPropertyName("account_id")]
	public string AccountId { get; set; }

	[JsonPropertyName("app")]
	public string App { get; set; }

	[JsonPropertyName("client_id")]
	public string ClientId { get; set; }

	[JsonPropertyName("client_service")]
	public string ClientService { get; set; }

	[JsonPropertyName("device_id")]
	public string DeviceId { get; set; }

	[JsonPropertyName("displayName")]
	public string DisplayName { get; set; }

	[JsonPropertyName("expires_at")]
	public string ExpiresAt { get; set; }

	[JsonPropertyName("expires_in")]
	public int ExpiresIn { get; set; }

	[JsonPropertyName("in_app_id")]
	public string InAppId { get; set; }

	[JsonPropertyName("internal_client")]
	public bool InternalClient { get; set; }

	[JsonPropertyName("refresh_expires")]
	public int RefreshExpires { get; set; }

	[JsonPropertyName("refresh_expires_at")]
	public string RefreshExpiresAt { get; set; }

	[JsonPropertyName("refresh_token")]
	public string RefreshToken { get; set; }

	[JsonPropertyName("token_type")]
	public string TokenType { get; set; }
}