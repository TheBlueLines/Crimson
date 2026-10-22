using System.Net;
using Crimson.Core;
using Serilog;

namespace Crimson.Tests;

public sealed class DownloadManagerTests
{
	[Fact]
	public async Task DownloadFileWithFallback_UsesNextMirrorAfterHttpFailure()
	{
		var requests = new List<Uri>();
		using var client = new HttpClient(new StubHttpMessageHandler((request, _) =>
		{
			requests.Add(request.RequestUri!);
			var response = request.RequestUri!.Host switch
			{
				"first.epicgamescdn.com" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
				"second.epicgamescdn.com" => new HttpResponseMessage(HttpStatusCode.OK)
				{
					Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 })
				},
				_ => new HttpResponseMessage(HttpStatusCode.NotFound)
			};
			return Task.FromResult(response);
		}));
		using var logger = new LoggerConfiguration().CreateLogger();
		var manager = new DownloadManager(logger, client);
		await manager.InitializeMirrors(["https://first.epicgamescdn.com/base", "https://second.epicgamescdn.com/base"]);
		var destination = CreateDestination();

		try
		{
			var success = await manager.DownloadFileWithFallback("chunks/one.chunk", destination);

			Assert.True(success);
			Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(destination));
			Assert.Collection(
				requests,
				request => Assert.Equal("https://first.epicgamescdn.com/base/chunks/one.chunk", request.AbsoluteUri),
				request => Assert.Equal("https://second.epicgamescdn.com/base/chunks/one.chunk", request.AbsoluteUri));
		}
		finally
		{
			DeleteDestinationDirectory(destination);
		}
	}

	[Fact]
	public async Task DownloadFileWithFallback_StopsAfterConfiguredAttempts()
	{
		var requestCount = 0;
		using var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
		{
			Interlocked.Increment(ref requestCount);
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
		}));
		using var logger = new LoggerConfiguration().CreateLogger();
		var manager = new DownloadManager(logger, client);
		await manager.InitializeMirrors(["https://first.epicgamescdn.com", "https://second.epicgamescdn.com"]);
		var destination = CreateDestination();

		try
		{
			var success = await manager.DownloadFileWithFallback(
				"one.chunk", destination, maxRetries: 2);

			Assert.False(success);
			Assert.Equal(4, requestCount);
			Assert.False(File.Exists(destination));
			Assert.False(File.Exists(destination + ".partial"));
		}
		finally
		{
			DeleteDestinationDirectory(destination);
		}
	}

	[Fact]
	public async Task DownloadFileWithFallback_CancellationStopsActiveRequest()
	{
		var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var client = new HttpClient(new StubHttpMessageHandler(async (_, cancellationToken) =>
		{
			requestStarted.SetResult();
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			return new HttpResponseMessage(HttpStatusCode.OK);
		}));
		using var logger = new LoggerConfiguration().CreateLogger();
		var manager = new DownloadManager(logger, client);
		await manager.InitializeMirrors(["https://first.epicgamescdn.com"]);
		var destination = CreateDestination();
		using var cancellation = new CancellationTokenSource();

		try
		{
			var download = manager.DownloadFileWithFallback(
				"one.chunk", destination, cancellationToken: cancellation.Token);
			await requestStarted.Task;
			await cancellation.CancelAsync();

			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
			Assert.False(File.Exists(destination));
			Assert.False(File.Exists(destination + ".partial"));
		}
		finally
		{
			DeleteDestinationDirectory(destination);
		}
	}

	[Fact]
	public async Task DownloadFileWithFallback_RejectsShortBodyAndUsesNextMirror()
	{
		using var client = new HttpClient(new StubHttpMessageHandler((request, _) =>
		{
			var bytes = request.RequestUri!.Host == "first.epicgamescdn.com"
				? new byte[] { 1, 2, 3 }
				: new byte[] { 4, 5, 6, 7 };
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new ByteArrayContent(bytes)
			});
		}));
		using var logger = new LoggerConfiguration().CreateLogger();
		var manager = new DownloadManager(logger, client);
		await manager.InitializeMirrors(["https://first.epicgamescdn.com", "https://second.epicgamescdn.com"]);
		var destination = CreateDestination();

		try
		{
			var success = await manager.DownloadFileWithFallback(
				"one.chunk", destination, expectedSize: 4);

			Assert.True(success);
			Assert.Equal(new byte[] { 4, 5, 6, 7 }, await File.ReadAllBytesAsync(destination));
			Assert.False(File.Exists(destination + ".partial"));
		}
		finally
		{
			DeleteDestinationDirectory(destination);
		}
	}

	[Fact]
	public async Task DownloadFileWithFallback_FailedAttemptDoesNotReplaceExistingDestination()
	{
		using var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
			Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
			})));
		using var logger = new LoggerConfiguration().CreateLogger();
		var manager = new DownloadManager(logger, client);
		await manager.InitializeMirrors(["https://first.epicgamescdn.com"]);
		var destination = CreateDestination();
		var original = new byte[] { 9, 8, 7, 6 };

		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
			await File.WriteAllBytesAsync(destination, original);

			var success = await manager.DownloadFileWithFallback(
				"one.chunk", destination, maxRetries: 1, expectedSize: 4);

			Assert.False(success);
			Assert.Equal(original, await File.ReadAllBytesAsync(destination));
			Assert.False(File.Exists(destination + ".partial"));
		}
		finally
		{
			DeleteDestinationDirectory(destination);
		}
	}

	[Fact]
	public async Task DownloadFileWithFallback_WithoutMirrorsFailsImmediately()
	{
		using var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
			throw new InvalidOperationException("No request should be made")));
		using var logger = new LoggerConfiguration().CreateLogger();
		var manager = new DownloadManager(logger, client);
		await manager.InitializeMirrors([]);
		var destination = CreateDestination();

		try
		{
			var success = await manager.DownloadFileWithFallback("one.chunk", destination);

			Assert.False(success);
			Assert.False(File.Exists(destination));
		}
		finally
		{
			DeleteDestinationDirectory(destination);
		}
	}

	private static string CreateDestination() =>
		Path.Combine(Path.GetTempPath(), $"crimson-test-{Guid.NewGuid():N}", "chunk.bin");

	private static void DeleteDestinationDirectory(string destination)
	{
		var directory = Path.GetDirectoryName(destination)!;
		if (Directory.Exists(directory))
			Directory.Delete(directory, recursive: true);
	}

	private sealed class StubHttpMessageHandler(
		Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
		: HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken) => send(request, cancellationToken);
	}
}
