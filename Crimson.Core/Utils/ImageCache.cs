using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Crimson.Utils;

/// <summary>
/// Disk cache for remote artwork. Downloads run a few at a time and newest request first, so
/// the covers a user just scrolled to are fetched before the ones they scrolled past; requests
/// cancelled before their download starts are dropped. A download that has started always
/// finishes and is cached, even if nobody waits for it any more.
/// </summary>
public sealed class ImageCache
{
	private readonly HttpClient _http;
	private readonly string _directory;
	private readonly ILogger _logger;
	private readonly int _maxConcurrentDownloads;

	private readonly object _lock = new();
	// Used as a stack: the last request added is the next one started.
	private readonly List<Request> _pending = [];
	private readonly Dictionary<string, Task<string>> _downloads = new(StringComparer.Ordinal);
	private int _active;

	public ImageCache(HttpClient http, string directory, ILogger logger, int maxConcurrentDownloads = 6)
	{
		_http = http;
		_directory = directory;
		_logger = logger;
		_maxConcurrentDownloads = Math.Max(1, maxConcurrentDownloads);
	}

	public string GetCachePath(string url) =>
		Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".img");

	/// <summary>Returns the local path of <paramref name="url"/>, downloading it if needed.</summary>
	public Task<string> GetAsync(string url, CancellationToken cancellationToken = default)
	{
		var path = GetCachePath(url);
		if (File.Exists(path))
			return Task.FromResult(path);
		if (cancellationToken.IsCancellationRequested)
			return Task.FromCanceled<string>(cancellationToken);

		lock (_lock)
		{
			if (_downloads.TryGetValue(url, out var running))
				return running.WaitAsync(cancellationToken);
		}

		var request = new Request(url, path, cancellationToken);
		request.Registration = cancellationToken.Register(() => Cancel(request));
		lock (_lock)
		{
			// The token may have fired before the request was listed; Cancel found nothing then.
			if (cancellationToken.IsCancellationRequested)
			{
				request.Registration.Dispose();
				return Task.FromCanceled<string>(cancellationToken);
			}
			_pending.Add(request);
		}

		Pump();
		return request.Completion.Task;
	}

	private void Cancel(Request request)
	{
		bool removed;
		lock (_lock)
			removed = _pending.Remove(request);
		if (removed)
			request.Completion.TrySetCanceled(request.Token);
	}

	private void Pump()
	{
		while (true)
		{
			Request next;
			Task<string> download;
			lock (_lock)
			{
				if (_pending.Count == 0)
					return;
				next = _pending[^1];
				if (!_downloads.TryGetValue(next.Url, out download!))
				{
					if (_active >= _maxConcurrentDownloads)
						return;
					_active++;
					download = Task.Run(() => RunDownloadAsync(next.Url, next.Path));
					_downloads[next.Url] = download;
				}
				_pending.RemoveAt(_pending.Count - 1);
			}

			_ = CompleteAsync(next, download);
		}
	}

	private static async Task CompleteAsync(Request request, Task<string> download)
	{
		try
		{
			request.Completion.TrySetResult(await download.ConfigureAwait(false));
		}
		catch (Exception ex)
		{
			request.Completion.TrySetException(ex);
		}
		finally
		{
			request.Registration.Dispose();
		}
	}

	private async Task<string> RunDownloadAsync(string url, string path)
	{
		try
		{
			if (File.Exists(path))
				return path;

			Directory.CreateDirectory(_directory);
			var bytes = await _http.GetByteArrayAsync(url).ConfigureAwait(false);
			var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
			await File.WriteAllBytesAsync(temporary, bytes).ConfigureAwait(false);
			File.Move(temporary, path, overwrite: true);
			return path;
		}
		catch (Exception ex)
		{
			_logger.Debug(ex, "ImageCache: failed to download {Url}", url);
			throw;
		}
		finally
		{
			lock (_lock)
			{
				_active--;
				_downloads.Remove(url);
			}
			Pump();
		}
	}

	private sealed class Request(string url, string path, CancellationToken token)
	{
		public string Url { get; } = url;
		public string Path { get; } = path;
		public CancellationToken Token { get; } = token;
		public TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public CancellationTokenRegistration Registration { get; set; }
	}
}
