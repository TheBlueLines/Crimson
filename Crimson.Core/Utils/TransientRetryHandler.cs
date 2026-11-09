using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Crimson.Utils;

/// <summary>
/// Retries requests that failed for a transient reason, giving each attempt its own timeout.
/// Backs off exponentially with jitter, and honours Retry-After when the server sends one.
/// </summary>
public sealed class TransientRetryHandler : DelegatingHandler
{
	private readonly int _maxRetries;
	private readonly TimeSpan _attemptTimeout;
	private readonly TimeSpan _baseDelay;

	public TransientRetryHandler(int maxRetries, TimeSpan attemptTimeout, TimeSpan baseDelay)
	{
		_maxRetries = maxRetries;
		_attemptTimeout = attemptTimeout;
		_baseDelay = baseDelay;
	}

	protected override async Task<HttpResponseMessage> SendAsync(
		HttpRequestMessage request,
		CancellationToken cancellationToken)
	{
		for (var attempt = 0; ; attempt++)
		{
			using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			attemptTimeout.CancelAfter(_attemptTimeout);

			HttpResponseMessage? response = null;
			try
			{
				response = await base.SendAsync(request, attemptTimeout.Token).ConfigureAwait(false);
				if (!IsTransient(response.StatusCode) || attempt >= _maxRetries)
					return response;
			}
			catch (HttpRequestException) when (attempt < _maxRetries)
			{
			}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
				if (attempt >= _maxRetries)
					throw new TimeoutException($"The request timed out after {_attemptTimeout.TotalSeconds:0} seconds.");
			}

			var delay = response?.Headers.RetryAfter is { } retryAfter
				? GetRetryAfterDelay(retryAfter)
				: GetBackoffDelay(attempt);
			response?.Dispose();
			await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
		}
	}

	private static bool IsTransient(HttpStatusCode status) =>
		(int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

	private static TimeSpan GetRetryAfterDelay(System.Net.Http.Headers.RetryConditionHeaderValue retryAfter)
	{
		var delay = retryAfter.Delta ?? (retryAfter.Date - DateTimeOffset.UtcNow) ?? TimeSpan.Zero;
		return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
	}

	private TimeSpan GetBackoffDelay(int attempt) =>
		_baseDelay * Math.Pow(2, attempt) * (0.75 + Random.Shared.NextDouble() * 0.5);
}
