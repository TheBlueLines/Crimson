using System.Net;
using System.Text;
using System.Text.Json;
using Crimson.Core;
using Crimson.Models;
using Crimson.Utils;
using Serilog;

namespace Crimson.Tests;

public sealed class AuthSessionTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"crimson-auth-session-{Guid.NewGuid():N}");
	private readonly Serilog.Core.Logger _logger = new LoggerConfiguration().CreateLogger();

	[Fact]
	public async Task LogoutWaitsForRefreshThenRemovesRefreshedCredentials()
	{
		var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var client = new HttpClient(new StubHttpMessageHandler(async request =>
		{
			if (request.Method == HttpMethod.Post)
			{
				refreshStarted.TrySetResult();
				await releaseRefresh.Task;
				return JsonResponse(CreateUser("fresh", DateTimeOffset.UtcNow.AddHours(1)));
			}

			return new HttpResponseMessage(HttpStatusCode.OK);
		}));
		var storage = new Storage(_logger, _root, Path.Combine(_root, "games"));
		await storage.SaveUserData(CreateUser("old", DateTimeOffset.UtcNow.AddHours(1)));
		var auth = new AuthManager(_logger, storage, new TestCredentialProtector(), client);
		Assert.Equal(AuthenticationStatus.LoggedIn, await auth.CheckAuthStatus());
		await storage.SaveUserData(CreateUser("old", DateTimeOffset.UtcNow.AddMinutes(-10)));

		var refresh = auth.GetAccessToken();
		await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		var logout = auth.Logout();
		releaseRefresh.TrySetResult();
		await Task.WhenAll(refresh, logout).WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(AuthenticationStatus.LoggedOut, auth.AuthenticationStatus);
		Assert.Null(await storage.GetUserData());
	}

	private static UserData CreateUser(string token, DateTimeOffset accessExpiry) => new()
	{
		AccessToken = token,
		RefreshToken = $"{token}-refresh",
		ExpiresAt = accessExpiry.ToString("O"),
		RefreshExpiresAt = DateTimeOffset.UtcNow.AddDays(1).ToString("O")
	};

	private static HttpResponseMessage JsonResponse(UserData user) => new(HttpStatusCode.OK)
	{
		Content = new StringContent(JsonSerializer.Serialize(user), Encoding.UTF8, "application/json")
	};

	public void Dispose()
	{
		_logger.Dispose();
		if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
	}

	private sealed class StubHttpMessageHandler(
		Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken) => send(request);
	}
}
