using System.Collections.Concurrent;
using System.Net;
using Crimson.Utils;
using Serilog;

namespace Crimson.Tests;

public sealed class ImageCacheTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"crimson-images-{Guid.NewGuid():N}");
	private readonly RecordingHandler _handler = new();
	private readonly Serilog.Core.Logger _logger = new LoggerConfiguration().CreateLogger();

	[Fact]
	public async Task CachedImageIsServedFromDisk()
	{
		var cache = CreateCache();

		var first = await cache.GetAsync("https://example.test/a.jpg");
		var second = await cache.GetAsync("https://example.test/a.jpg");

		Assert.Equal(first, second);
		Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(first));
		Assert.Single(_handler.Requests);
	}

	[Fact]
	public async Task NewestRequestIsDownloadedFirst()
	{
		var cache = CreateCache(maxConcurrentDownloads: 1);
		var gate = _handler.Block("https://example.test/busy.jpg");
		var busy = cache.GetAsync("https://example.test/busy.jpg");
		await _handler.WaitForRequest("https://example.test/busy.jpg");

		var older = cache.GetAsync("https://example.test/older.jpg");
		var newer = cache.GetAsync("https://example.test/newer.jpg");
		gate.SetResult();
		await Task.WhenAll(busy, older, newer);

		Assert.Equal(
			["https://example.test/busy.jpg", "https://example.test/newer.jpg", "https://example.test/older.jpg"],
			_handler.Requests);
	}

	[Fact]
	public async Task CancelledRequestIsNeverDownloaded()
	{
		var cache = CreateCache(maxConcurrentDownloads: 1);
		var gate = _handler.Block("https://example.test/busy.jpg");
		var busy = cache.GetAsync("https://example.test/busy.jpg");
		await _handler.WaitForRequest("https://example.test/busy.jpg");

		using var cancellation = new CancellationTokenSource();
		var scrolledPast = cache.GetAsync("https://example.test/gone.jpg", cancellation.Token);
		cancellation.Cancel();
		gate.SetResult();
		await busy;

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scrolledPast);
		Assert.DoesNotContain("https://example.test/gone.jpg", _handler.Requests);
	}

	[Fact]
	public async Task FailedDownloadIsNotCached()
	{
		var cache = CreateCache();
		_handler.Fail("https://example.test/missing.jpg");

		await Assert.ThrowsAsync<HttpRequestException>(() => cache.GetAsync("https://example.test/missing.jpg"));

		Assert.False(File.Exists(cache.GetCachePath("https://example.test/missing.jpg")));
	}

	public void Dispose()
	{
		_logger.Dispose();
		try
		{
			if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
		}
	}

	private ImageCache CreateCache(int maxConcurrentDownloads = 6) =>
		new(new HttpClient(_handler), _root, _logger, maxConcurrentDownloads);

	private sealed class RecordingHandler : HttpMessageHandler
	{
		private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new();
		private readonly ConcurrentDictionary<string, TaskCompletionSource> _seen = new();
		private readonly ConcurrentDictionary<string, byte> _failing = new();
		private readonly object _lock = new();
		private readonly List<string> _requests = [];

		public List<string> Requests
		{
			get
			{
				lock (_lock)
					return [.. _requests];
			}
		}

		public TaskCompletionSource Block(string url) =>
			_gates.GetOrAdd(url, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

		public void Fail(string url) => _failing[url] = 0;

		public Task WaitForRequest(string url) => Seen(url).Task.WaitAsync(TimeSpan.FromSeconds(5));

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var url = request.RequestUri!.ToString();
			lock (_lock)
				_requests.Add(url);
			Seen(url).TrySetResult();
			if (_gates.TryGetValue(url, out var gate))
				await gate.Task;

			return _failing.ContainsKey(url)
				? new HttpResponseMessage(HttpStatusCode.NotFound)
				: new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
		}

		private TaskCompletionSource Seen(string url) =>
			_seen.GetOrAdd(url, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
	}
}
