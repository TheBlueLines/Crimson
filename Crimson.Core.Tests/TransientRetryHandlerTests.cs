using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Utils;
using Xunit;

namespace Crimson.Core.Tests;

public class TransientRetryHandlerTests
{
	private static HttpClient CreateClient(
		StubHandler stub,
		int maxRetries = 2,
		TimeSpan? attemptTimeout = null) =>
		new(new TransientRetryHandler(maxRetries, attemptTimeout ?? TimeSpan.FromSeconds(5), TimeSpan.Zero)
		{
			InnerHandler = stub
		});

	[Theory]
	[InlineData(HttpStatusCode.InternalServerError)]
	[InlineData(HttpStatusCode.ServiceUnavailable)]
	[InlineData(HttpStatusCode.RequestTimeout)]
	[InlineData(HttpStatusCode.TooManyRequests)]
	public async Task RetriesTransientStatusUntilSuccess(HttpStatusCode status)
	{
		var stub = new StubHandler(_ => new HttpResponseMessage(status), _ => new HttpResponseMessage(HttpStatusCode.OK));

		var response = await CreateClient(stub).GetAsync("https://example.test/");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal(2, stub.Calls);
	}

	[Theory]
	[InlineData(HttpStatusCode.BadRequest)]
	[InlineData(HttpStatusCode.Unauthorized)]
	[InlineData(HttpStatusCode.NotFound)]
	public async Task DoesNotRetryPermanentStatus(HttpStatusCode status)
	{
		var stub = new StubHandler(_ => new HttpResponseMessage(status));

		var response = await CreateClient(stub).GetAsync("https://example.test/");

		Assert.Equal(status, response.StatusCode);
		Assert.Equal(1, stub.Calls);
	}

	[Fact]
	public async Task ReturnsLastResponseWhenRetriesAreExhausted()
	{
		var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));

		var response = await CreateClient(stub, maxRetries: 3).GetAsync("https://example.test/");

		Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
		Assert.Equal(4, stub.Calls);
	}

	[Fact]
	public async Task RetriesConnectionFailuresAndRethrowsTheLastOne()
	{
		var stub = new StubHandler(_ => Task.FromException<HttpResponseMessage>(new HttpRequestException("unreachable")));

		await Assert.ThrowsAsync<HttpRequestException>(() => CreateClient(stub).GetAsync("https://example.test/"));

		Assert.Equal(3, stub.Calls);
	}

	[Fact]
	public async Task EachAttemptGetsItsOwnTimeout()
	{
		var calls = 0;
		var stub = new StubHandler(async token =>
		{
			if (Interlocked.Increment(ref calls) == 1)
				await Task.Delay(Timeout.Infinite, token);
			return new HttpResponseMessage(HttpStatusCode.OK);
		});

		var response = await CreateClient(stub, attemptTimeout: TimeSpan.FromMilliseconds(50))
			.GetAsync("https://example.test/");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal(2, calls);
	}

	[Fact]
	public async Task ReportsATimeoutWhenEveryAttemptTimesOut()
	{
		var stub = new StubHandler(async token =>
		{
			await Task.Delay(Timeout.Infinite, token);
			return new HttpResponseMessage(HttpStatusCode.OK);
		});

		await Assert.ThrowsAsync<TimeoutException>(() =>
			CreateClient(stub, maxRetries: 1, attemptTimeout: TimeSpan.FromMilliseconds(20))
				.GetAsync("https://example.test/"));

		Assert.Equal(2, stub.Calls);
	}

	[Fact]
	public async Task CallerCancellationIsNotRetried()
	{
		using var cancellation = new CancellationTokenSource();
		var stub = new StubHandler(async token =>
		{
			await cancellation.CancelAsync();
			await Task.Delay(Timeout.Infinite, token);
			return new HttpResponseMessage(HttpStatusCode.OK);
		});

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			CreateClient(stub).GetAsync("https://example.test/", cancellation.Token));

		Assert.Equal(1, stub.Calls);
	}

	[Fact]
	public async Task HonoursRetryAfterOverTheBackoff()
	{
		var first = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
		first.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1000));
		var stub = new StubHandler(_ => first, _ => new HttpResponseMessage(HttpStatusCode.OK));

		var started = DateTime.UtcNow;
		var response = await CreateClient(stub).GetAsync("https://example.test/");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(900));
	}

	private sealed class StubHandler : HttpMessageHandler
	{
		private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _scripted = new();
		private readonly Func<CancellationToken, Task<HttpResponseMessage>> _last;

		public int Calls { get; private set; }

		public StubHandler(params Func<CancellationToken, HttpResponseMessage>[] responses)
		{
			foreach (var response in responses)
				_scripted.Enqueue(token => Task.FromResult(response(token)));
			_last = _scripted.ToArray()[^1];
		}

		public StubHandler(Func<CancellationToken, Task<HttpResponseMessage>> response) => _last = response;

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Calls++;
			var next = _scripted.Count > 0 ? _scripted.Dequeue() : _last;
			return next(cancellationToken);
		}
	}
}
