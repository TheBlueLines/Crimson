using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Crimson.Models;

namespace Crimson.Core;

public class DownloadManager
{
	private readonly Dictionary<string, MirrorStats> _mirrorStats = new();
	private readonly ILogger _log;
	private readonly HttpClient _httpClient;
	private readonly SemaphoreSlim _statLock = new(1);


	public DownloadManager(ILogger log, HttpClient httpClient)
	{
		_log = log;
		_httpClient = httpClient;
	}

	public async Task InitializeMirrors(List<string> baseUrls)
	{
		await _statLock.WaitAsync();
		try
		{
			_mirrorStats.Clear();
			foreach (var value in baseUrls)
			{
				// Skip, never throw. Base URLs can come from metadata cached on disk by an older
				// build, so an unrecognised CDN host must not take the whole install down.
				if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
					!EpicEndpointPolicy.IsAllowedContentUri(uri))
				{
					_log.Warning("Skipping unapproved mirror host {Host}", uri?.Host ?? "invalid");
					continue;
				}

				var baseUrl = uri.AbsoluteUri.TrimEnd('/');
				_mirrorStats[baseUrl] = new MirrorStats
				{
					BaseUrl = baseUrl,
					FailureCount = 0,
					AverageSpeed = 0,
					LastAttempt = DateTime.MinValue
				};
			}
		}
		finally
		{
			_statLock.Release();
		}
	}

	/// <summary>
	/// Attempts each configured mirror once per retry round and publishes only a complete download.
	/// </summary>
	public async Task<bool> DownloadFileWithFallback(
		string relativePath,
		string destinationPath,
		int maxRetries = 7,
		CancellationToken cancellationToken = default,
		long? expectedSize = null)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(maxRetries, 1);

		for (var attempt = 1; attempt <= maxRetries; attempt++)
		{
			var orderedMirrors = await GetPrioritizedMirrors(cancellationToken);
			if (orderedMirrors.Count == 0)
			{
				_log.Error("DownloadFileWithFallback: No download mirrors are available");
				return false;
			}

			foreach (var mirror in orderedMirrors)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var fullUrl = new Uri(new Uri($"{mirror.BaseUrl.TrimEnd('/')}/"), relativePath.TrimStart('/')).AbsoluteUri;

				try
				{
					var success = await MeasureDownloadSpeed(
						mirror, fullUrl, destinationPath, expectedSize, cancellationToken);
					if (success) return true;

					await UpdateMirrorStats(mirror.BaseUrl, false, 0, cancellationToken);
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					throw;
				}
				catch (Exception ex)
				{
					_log.Error(
						"Attempt {Attempt}/{MaxAttempts} failed for mirror {Mirror} with {ErrorType}",
						attempt,
						maxRetries,
						SensitiveDataRedactor.UriWithoutQuery(mirror.BaseUrl),
						ex.GetType().Name);
					await UpdateMirrorStats(mirror.BaseUrl, false, 0, cancellationToken);
				}
			}

			// Exponential backoff, capped. A flat 100ms burned all attempts inside a second and
			// turned a brief network blip into a failed install.
			if (attempt < maxRetries)
				await Task.Delay(
					TimeSpan.FromMilliseconds(Math.Min(500 * Math.Pow(2, attempt - 1), 15_000)),
					cancellationToken);
		}

		return false;
	}

	private async Task<List<MirrorStats>> GetPrioritizedMirrors(CancellationToken cancellationToken = default)
	{
		await _statLock.WaitAsync(cancellationToken);
		try
		{
			return _mirrorStats.Values
				.OrderByDescending(m => m.AverageSpeed)
				.ThenBy(m => m.FailureCount)
				.ToList();
		}
		finally
		{
			_statLock.Release();
		}
	}

	private async Task<bool> MeasureDownloadSpeed(
		MirrorStats mirror,
		string url,
		string destinationPath,
		long? expectedSize,
		CancellationToken cancellationToken)
	{
		var stopwatch = Stopwatch.StartNew();
		var partialPath = destinationPath + ".partial";
		try
		{
			var uri = EpicEndpointPolicy.RequireContentUri(url);
			using var response = await _httpClient.GetAsync(
				uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
			if (!response.IsSuccessStatusCode)
			{
				_log.Warning(
					"MeasureDownloadSpeed: HTTP {StatusCode} for {Url}",
					(int)response.StatusCode,
					SensitiveDataRedactor.UriWithoutQuery(uri.AbsoluteUri));
				return false;
			}

			var directoryPath = Path.GetDirectoryName(destinationPath);
			if (!string.IsNullOrEmpty(directoryPath))
				Directory.CreateDirectory(directoryPath);

			await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
			await using (var fileStream = new FileStream(
				partialPath, FileMode.Create, FileAccess.Write, FileShare.None,
				bufferSize: 81920, useAsync: true))
			{
				await stream.CopyToAsync(fileStream, cancellationToken);
				await fileStream.FlushAsync(cancellationToken);
			}

			var downloadedSize = new FileInfo(partialPath).Length;
			var contentLength = response.Content.Headers.ContentLength;
			if ((contentLength.HasValue && downloadedSize != contentLength.Value) ||
				(expectedSize.HasValue && downloadedSize != expectedSize.Value))
			{
				_log.Warning(
					"MeasureDownloadSpeed: Size mismatch for {Url}. Downloaded {DownloadedSize}, expected {ExpectedSize}, content length {ContentLength}",
					SensitiveDataRedactor.UriWithoutQuery(uri.AbsoluteUri),
					downloadedSize,
					expectedSize,
					contentLength);
				return false;
			}

			File.Move(partialPath, destinationPath, overwrite: true);

			stopwatch.Stop();
			var elapsedSeconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
			var speedMbps = (downloadedSize / 1024.0 / 1024.0) / elapsedSeconds;
			await UpdateMirrorStats(mirror.BaseUrl, true, speedMbps);
			return true;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			stopwatch.Stop();
			_log.Error(
				"MeasureDownloadSpeed failed for {Url} with {ErrorType}",
				SensitiveDataRedactor.UriWithoutQuery(url),
				ex.GetType().Name);
			throw;
		}
		finally
		{
			try
			{
				File.Delete(partialPath);
			}
			catch (Exception ex)
			{
				_log.Warning(ex, "MeasureDownloadSpeed: Failed to remove partial download {Path}", partialPath);
			}
		}
	}

	private async Task UpdateMirrorStats(
		string baseUrl,
		bool success,
		double speed,
		CancellationToken cancellationToken = default)
	{
		await _statLock.WaitAsync(cancellationToken);
		try
		{
			if (_mirrorStats.TryGetValue(baseUrl, out var stats))
			{
				if (!success)
				{
					stats.FailureCount++;
				}
				else if (speed > 0)
				{
					stats.AverageSpeed = stats.AverageSpeed == 0
						? speed
						: (stats.AverageSpeed * 0.7 + speed * 0.3); // Weighted average
				}
				stats.LastAttempt = DateTime.UtcNow;
			}
		}
		finally
		{
			_statLock.Release();
		}
	}
}
