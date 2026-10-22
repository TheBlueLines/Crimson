using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using Crimson.Core;
using Crimson.Utils;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Crimson.Tests;

public sealed class SecurityContainmentTests
{
	[Theory]
	[InlineData("http://download.epicgames.com/file.chunk")]
	[InlineData("https://download.epicgames.com.evil.test/file.chunk")]
	[InlineData("https://user@download.epicgames.com/file.chunk")]
	[InlineData("https://download.epicgames.com:444/file.chunk")]
	public void ContentPolicy_RejectsUnapprovedUris(string value)
	{
		Assert.Throws<InvalidOperationException>(() => EpicEndpointPolicy.RequireContentUri(value));
	}

	[Theory]
	[InlineData("http://store.epicgames.com/graphql")]
	[InlineData("https://store.epicgames.com.evil.test/graphql")]
	[InlineData("https://user@store.epicgames.com/graphql")]
	[InlineData("https://store.epicgames.com:444/graphql")]
	public void StorePolicy_RejectsUnapprovedUris(string value)
	{
		Assert.Throws<InvalidOperationException>(() => EpicEndpointPolicy.RequireStoreUri(value));
	}

	[Fact]
	public void ApiPolicy_AllowsOwnershipTokenHost()
	{
		var uri = EpicEndpointPolicy.RequireApiUri(
			"https://ecommerceintegration-public-service-ecomprod02.ol.epicgames.com/ownershipToken");

		Assert.Equal("ecommerceintegration-public-service-ecomprod02.ol.epicgames.com", uri.Host);
	}

	[Theory]
	[InlineData("http://ecommerceintegration-public-service-ecomprod02.ol.epicgames.com/ownershipToken")]
	[InlineData("https://ecommerceintegration-public-service-ecomprod02.ol.epicgames.com.evil.test/ownershipToken")]
	public void ApiPolicy_RejectsUnapprovedOwnershipUris(string value)
	{
		Assert.Throws<InvalidOperationException>(() => EpicEndpointPolicy.RequireApiUri(value));
	}

	[Fact]
	public void StorePolicy_AllowsLauncherGraphQlHost()
	{
		var uri = EpicEndpointPolicy.RequireStoreUri("https://launcher.store.epicgames.com/graphql");

		Assert.Equal("launcher.store.epicgames.com", uri.Host);
	}

	[Fact]
	public void LoginMessageGate_ValidatesOriginSchemaBoundsAndReplay()
	{
		var gate = new EpicLoginMessageGate();
		const string valid = "{\"type\":\"set_exchange_code\",\"code\":\"Code_123-abc\"}";

		Assert.True(gate.TryAccept("https://www.epicgames.com/id/login", valid, out var code));
		Assert.Equal("Code_123-abc", code);
		Assert.False(gate.TryAccept("https://www.epicgames.com/id/login", valid, out _));
		Assert.False(gate.TryAccept("https://www.epicgames.com.evil.test/id/login", valid, out _));
		Assert.False(gate.TryAccept(
			"https://www.epicgames.com/id/login",
			"{\"type\":\"unexpected\",\"code\":\"Code_456\"}",
			out _));
		Assert.False(gate.TryAccept(
			"https://www.epicgames.com/id/login",
			"{\"type\":\"set_exchange_code\",\"code\":\"Code_456\",\"extra\":true}",
			out _));
		Assert.False(gate.TryAccept(
			"https://www.epicgames.com/id/login",
			new string('x', EpicLoginMessageGate.MaximumMessageLength + 1),
			out _));
	}

	[Fact]
	public void Redactor_RemovesSignedQueriesAndSensitiveFields()
	{
		const string secret = "CANARY_SECRET_4f29";
		var uri = $"https://download.epicgames.com/chunk?token={secret}&signature={secret}";
		var fields = $"access_token={secret} refresh_token:{secret} password=\"{secret}\"";

		var redactedUri = SensitiveDataRedactor.UriWithoutQuery(uri);
		var redactedFields = SensitiveDataRedactor.Fields(fields);

		Assert.Equal("https://download.epicgames.com/chunk", redactedUri);
		Assert.DoesNotContain(secret, redactedUri, StringComparison.Ordinal);
		Assert.DoesNotContain(secret, redactedFields, StringComparison.Ordinal);
		Assert.Contains(SensitiveDataRedactor.Redacted, redactedFields, StringComparison.Ordinal);
	}

	[Fact]
	public async Task OAuthFailure_DoesNotMutateDefaultAuthorizationOrLogSecrets()
	{
		const string exchangeCanary = "ExchangeCanary_4f29";
		const string responseCanary = "ResponseCanary_9a11";
		AuthenticationHeaderValue? authorization = null;
		string? body = null;
		using var client = new HttpClient(new StubHttpMessageHandler(async request =>
		{
			authorization = request.Headers.Authorization;
			body = await request.Content!.ReadAsStringAsync();
			return new HttpResponseMessage(HttpStatusCode.BadRequest)
			{
				Content = new StringContent($"{{\"access_token\":\"{responseCanary}\"}}")
			};
		}));
		var sink = new CapturingSink();
		using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
		var storage = (Storage)RuntimeHelpers.GetUninitializedObject(typeof(Storage));
		var manager = new AuthManager(
			logger,
			storage,
			new TestCredentialProtector(),
			client);

		await manager.DoExchangeLogin(exchangeCanary);

		Assert.Equal("Basic", authorization?.Scheme);
		Assert.Contains(exchangeCanary, body, StringComparison.Ordinal);
		Assert.Null(client.DefaultRequestHeaders.Authorization);
		Assert.DoesNotContain(exchangeCanary, sink.Messages, StringComparison.Ordinal);
		Assert.DoesNotContain(responseCanary, sink.Messages, StringComparison.Ordinal);
	}

	private sealed class StubHttpMessageHandler(
		Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken) => send(request);
	}

	private sealed class CapturingSink : ILogEventSink
	{
		private readonly List<string> _messages = [];

		public string Messages => string.Join(Environment.NewLine, _messages);

		public void Emit(LogEvent logEvent) => _messages.Add(logEvent.RenderMessage());
	}
}
