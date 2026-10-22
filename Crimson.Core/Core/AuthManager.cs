using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Models;
using Crimson.Utils;
using Serilog;

namespace Crimson.Core;

public class AuthManager
{
	private readonly ILogger _log;
	private readonly Storage _storage;
	private readonly ICredentialProtector _credentialProtector;

	private AuthenticationStatus _authenticationStatus;
	private readonly SemaphoreSlim _refreshLock = new(1, 1);

	private const string BasicAuthUsername = "34a02cf8f4414e29b15921876da36f9a";
	private const string BasicAuthPassword = "daafbccc737745039dffe53d94fc76cf";
	private const string OAuthHost = "https://account-public-service-prod03.ol.epicgames.com";
	private const string UserAgent = "UELauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit";
	private static readonly TimeSpan TokenRefreshBuffer = TimeSpan.FromMinutes(5);

	private readonly HttpClient _httpClient;


	public delegate void AuthStatusChangedEventHandler(object sender, AuthStatusChangedEventArgs e);

	public event AuthStatusChangedEventHandler AuthStatusChanged;

	public AuthenticationStatus AuthenticationStatus => _authenticationStatus;


	public AuthManager(
		ILogger log,
		Storage storage,
		ICredentialProtector credentialProtector,
		HttpClient httpClient)
	{
		_log = log;
		_storage = storage;
		_credentialProtector = credentialProtector;
		_httpClient = httpClient;
	}

	// <summary>
	// Check if the user is logged in or not
	// </summary>
	public async Task<AuthenticationStatus> CheckAuthStatus()
	{
		await _refreshLock.WaitAsync();
		try
		{
			return await CheckAuthStatusCore();
		}
		finally
		{
			_refreshLock.Release();
		}
	}

	private async Task<AuthenticationStatus> CheckAuthStatusCore()
	{
		try
		{
			_authenticationStatus = AuthenticationStatus.Checking;
			OnAuthStatusChanged(new AuthStatusChangedEventArgs(_authenticationStatus));

			var userData = await _storage.GetUserData();
			if (userData?.AccessToken is null)
			{
				if (userData is not null)
					_log.Error("CheckAuthStatus: Failed to parse user data from string");
				return await SetLoggedOutAsync();
			}

			userData.AccessToken = _credentialProtector.Unprotect(userData.AccessToken);
			userData.RefreshToken = _credentialProtector.Unprotect(userData.RefreshToken);
			if (DateTimeOffset.Parse(userData.RefreshExpiresAt) < DateTimeOffset.UtcNow)
			{
				_log.Information("CheckAuthStatus: Refresh token expired, logging out");
				return await SetLoggedOutAsync();
			}

			var accessToken = userData.AccessToken;
			if (DateTimeOffset.Parse(userData.ExpiresAt) < DateTimeOffset.UtcNow + TokenRefreshBuffer)
			{
				_log.Information("CheckAuthStatus: Access token expired or expiring soon, refreshing");
				var refreshed = await RequestTokens("refresh_token", "refresh_token", userData.RefreshToken);
				if (refreshed?.AccessToken is null)
				{
					_log.Error("CheckAuthStatus: Token refresh failed, logging out");
					return await SetLoggedOutAsync();
				}

				accessToken = refreshed.AccessToken;
				refreshed.AccessToken = _credentialProtector.Protect(refreshed.AccessToken);
				refreshed.RefreshToken = _credentialProtector.Protect(refreshed.RefreshToken);
				await _storage.SaveUserData(refreshed);
			}
			else
			{
				_log.Information("CheckAuthStatus: Access token is still valid");
			}

			if (!await VerifyAccessToken(accessToken))
			{
				_log.Warning("CheckAuthStatus: Access token is invalid, logging out");
				return await SetLoggedOutAsync();
			}

			_authenticationStatus = AuthenticationStatus.LoggedIn;
			OnAuthStatusChanged(new AuthStatusChangedEventArgs(_authenticationStatus));
			return _authenticationStatus;
		}
		catch (Exception ex)
		{
			_log.Error("CheckAuthStatus failed with {ErrorType}", ex.GetType().Name);
			return await SetLoggedOutAsync();
		}
	}

	/// <summary>
	/// Fetch user data from the exchange code
	/// </summary>
	public async Task DoExchangeLogin(string exchangeCode)
	{
		await _refreshLock.WaitAsync();
		try
		{
			await DoExchangeLoginCore(exchangeCode);
		}
		finally
		{
			_refreshLock.Release();
		}
	}

	private async Task DoExchangeLoginCore(string exchangeCode)
	{
		try
		{
			var userData = await RequestTokens("exchange_code", "exchange_code", exchangeCode);

			if (userData == null || userData.AccessToken == null)
			{
				_log.Error("DoExchangeLogin: Failed to fetch tokens");
				_authenticationStatus = AuthenticationStatus.LoginFailed;
				OnAuthStatusChanged(new AuthStatusChangedEventArgs(_authenticationStatus));
				return;
			}

			userData.AccessToken = _credentialProtector.Protect(userData.AccessToken);
			userData.RefreshToken = _credentialProtector.Protect(userData.RefreshToken);
			_log.Information("RequestTokens: Tokens successfully encrypted");

			await _storage.SaveUserData(userData);

			_authenticationStatus = AuthenticationStatus.LoggedIn;
			OnAuthStatusChanged(new AuthStatusChangedEventArgs(AuthenticationStatus.LoggedIn));
		}
		catch (Exception ex)
		{
			_log.Error("DoExchangeLogin failed with {ErrorType}", ex.GetType().Name);
			_authenticationStatus = AuthenticationStatus.LoginFailed;
			OnAuthStatusChanged(new AuthStatusChangedEventArgs(_authenticationStatus));
		}
	}

	public async Task<string> GetAccessToken()
	{
		if (_authenticationStatus != AuthenticationStatus.LoggedIn)
		{
			_log.Error("GetAccessToken: User is not logged in");
			return null;
		}

		await _refreshLock.WaitAsync();
		try
		{
			var userData = await _storage.GetUserData();
			if (userData == null) return null;

			var plainAccessToken = _credentialProtector.Unprotect(userData.AccessToken);
			var plainRefreshToken = _credentialProtector.Unprotect(userData.RefreshToken);

			var expiryDate = DateTimeOffset.Parse(userData.ExpiresAt);
			if (expiryDate < DateTimeOffset.UtcNow + TokenRefreshBuffer)
			{
				_log.Information("GetAccessToken: Token expired or expiring soon, refreshing");

				var refreshExpiryDate = DateTimeOffset.Parse(userData.RefreshExpiresAt);
				if (refreshExpiryDate < DateTimeOffset.UtcNow)
				{
					_log.Error("GetAccessToken: Refresh token also expired, logging out");
					await SetLoggedOutAsync();
					return null;
				}

				var newData = await RequestTokens("refresh_token", "refresh_token", plainRefreshToken);
				if (newData == null || newData.AccessToken == null)
				{
					_log.Error("GetAccessToken: Token refresh failed, logging out");
					await SetLoggedOutAsync();
					return null;
				}

				plainAccessToken = newData.AccessToken;
				newData.AccessToken = _credentialProtector.Protect(newData.AccessToken);
				newData.RefreshToken = _credentialProtector.Protect(newData.RefreshToken);
				await _storage.SaveUserData(newData);
			}

			return plainAccessToken;
		}
		catch (Exception ex)
		{
			_log.Error("GetAccessToken failed with {ErrorType}", ex.GetType().Name);
			return null;
		}
		finally
		{
			_refreshLock.Release();
		}
	}

	public async Task<UserData> GetUserData()
	{
		if (_authenticationStatus != AuthenticationStatus.LoggedIn)
		{
			_log.Error("GetUserData: User is not logged in");
			return null;
		}
		return await _storage.GetUserData();
	}

	public async Task Logout()
	{
		_log.Information("Logout: Logging out");
		await _refreshLock.WaitAsync();
		try
		{
			await SetLoggedOutAsync();
		}
		finally
		{
			_refreshLock.Release();
		}
	}

	private async Task<AuthenticationStatus> SetLoggedOutAsync()
	{
		await _storage.ClearUserData();
		_authenticationStatus = AuthenticationStatus.LoggedOut;
		OnAuthStatusChanged(new AuthStatusChangedEventArgs(AuthenticationStatus.LoggedOut));
		return _authenticationStatus;
	}

	private async Task<UserData> RequestTokens(string grantType, string codeName, string codeValue)
	{
		var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{BasicAuthUsername}:{BasicAuthPassword}"));
		using var formData = new FormUrlEncodedContent(new[]
		{
			new KeyValuePair<string, string>(codeName, codeValue),
			new KeyValuePair<string, string>("grant_type", grantType),
			new KeyValuePair<string, string>("token_type", "eg1")
		});
		using var request = new HttpRequestMessage(
			HttpMethod.Post,
			EpicEndpointPolicy.RequireApiUri($"{OAuthHost}/account/api/oauth/token"))
		{
			Content = formData
		};
		request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

		try
		{
			using var response = await _httpClient.SendAsync(request);
			if (!response.IsSuccessStatusCode)
			{
				_log.Error(
					"RequestTokens failed with HTTP {StatusCode} {ReasonPhrase}",
					(int)response.StatusCode,
					response.ReasonPhrase);
				return null;
			}

			var result = await response.Content.ReadAsStringAsync();
			var userData = JsonSerializer.Deserialize<UserData>(result);
			if (userData?.AccessToken == null)
			{
				_log.Error("RequestTokens returned an invalid authentication response");
				return null;
			}

			return userData;
		}
		catch (Exception ex)
		{
			_log.Error("RequestTokens failed with {ErrorType}", ex.GetType().Name);
			return null;
		}
	}

	// <summary>
	// Verify the access token is still valid
	// </summary>
	private async Task<bool> VerifyAccessToken(string accessToken)
	{
		using var request = new HttpRequestMessage(
			HttpMethod.Get,
			EpicEndpointPolicy.RequireApiUri($"{OAuthHost}/account/api/oauth/verify"));
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

		try
		{
			using var response = await _httpClient.SendAsync(request);
			return response.IsSuccessStatusCode;
		}
		catch (Exception ex)
		{
			_log.Error("VerifyAccessToken failed with {ErrorType}", ex.GetType().Name);
			return false;
		}
	}


	// Wrap event invocations inside a protected virtual method
	// to allow derived classes to override the event invocation behavior.
	// Wrap event invocations inside a private static method.
	private void OnAuthStatusChanged(AuthStatusChangedEventArgs e)
	{
		AuthStatusChanged?.Invoke(null, e);
	}
}

public enum AuthenticationStatus
{
	Checking,
	LoggedOut,
	LoggedIn,
	LoginFailed
}

public class AuthStatusChangedEventArgs(AuthenticationStatus newStatus) : EventArgs
{
	public AuthenticationStatus NewStatus { get; } = newStatus;
}

public class EpicLoginResponse
{
	[JsonPropertyName("type")] public string Type { get; set; }
	[JsonPropertyName("code")] public string Code { get; set; }
}
