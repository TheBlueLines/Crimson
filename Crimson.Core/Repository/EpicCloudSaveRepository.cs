using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Core;

namespace Crimson.Repository;

internal sealed class EpicCloudSaveRepository : ICloudSaveRepository
{
	private const string DataStorageHost = "datastorage-public-service-liveegs.live.use1a.on.epicgames.com";

	private readonly AuthManager _authManager;
	private readonly HttpClient _apiClient;
	private readonly HttpClient _contentClient;

	public EpicCloudSaveRepository(AuthManager authManager, HttpClient apiClient, HttpClient contentClient)
	{
		_authManager = authManager;
		_apiClient = apiClient;
		_contentClient = contentClient;
	}

	public async Task<IReadOnlyDictionary<string, CloudSaveRemoteFile>> ListFilesAsync(
		string accountId,
		string appName,
		CancellationToken cancellationToken)
	{
		using var request = await CreateRequestAsync(HttpMethod.Get, accountId, appName);
		using var response = await _apiClient.SendAsync(request, cancellationToken);
		if (response.StatusCode == HttpStatusCode.NotFound)
			return new Dictionary<string, CloudSaveRemoteFile>();
		return await ReadFilesAsync(response, cancellationToken);
	}

	public async Task<IReadOnlyDictionary<string, CloudSaveRemoteFile>> CreateFilesAsync(
		string accountId,
		string appName,
		IReadOnlyList<string> remotePaths,
		CancellationToken cancellationToken)
	{
		using var request = await CreateRequestAsync(HttpMethod.Post, accountId, appName);
		request.Content = JsonContent.Create(new { files = remotePaths });
		using var response = await _apiClient.SendAsync(request, cancellationToken);
		return await ReadFilesAsync(response, cancellationToken);
	}

	public async Task<byte[]> DownloadAsync(string readLink, CancellationToken cancellationToken)
	{
		// Signed links carry their own authorization. The Epic bearer token must never reach them.
		var uri = EpicEndpointPolicy.RequireCloudSaveLinkUri(readLink);
		using var response = await _contentClient.GetAsync(uri, cancellationToken);
		response.EnsureSuccessStatusCode();
		return await response.Content.ReadAsByteArrayAsync(cancellationToken);
	}

	public async Task UploadFileAsync(string writeLink, string localFile, CancellationToken cancellationToken)
	{
		var uri = EpicEndpointPolicy.RequireCloudSaveLinkUri(writeLink);
		var bytes = await File.ReadAllBytesAsync(localFile, cancellationToken);
		using var content = new ByteArrayContent(bytes);
		using var response = await _contentClient.PutAsync(uri, content, cancellationToken);
		response.EnsureSuccessStatusCode();
	}

	private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string accountId, string appName)
	{
		var accessToken = await _authManager.GetAccessToken();
		var uri = $"https://{DataStorageHost}/api/v1/access/egstore/savesync/{Uri.EscapeDataString(accountId)}/{Uri.EscapeDataString(appName)}/";
		var request = new HttpRequestMessage(method, EpicEndpointPolicy.RequireApiUri(uri));
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
		return request;
	}

	private static async Task<IReadOnlyDictionary<string, CloudSaveRemoteFile>> ReadFilesAsync(
		HttpResponseMessage response,
		CancellationToken cancellationToken)
	{
		response.EnsureSuccessStatusCode();
		var listing = await response.Content.ReadFromJsonAsync<FileListing>(cancellationToken);
		return (listing?.Files ?? []).ToDictionary(
			entry => entry.Key,
			entry => new CloudSaveRemoteFile(entry.Value.ReadLink, entry.Value.WriteLink),
			StringComparer.Ordinal);
	}

	private sealed class FileListing
	{
		[JsonPropertyName("files")]
		public Dictionary<string, FileLinks>? Files { get; set; }
	}

	private sealed class FileLinks
	{
		[JsonPropertyName("readLink")]
		public string? ReadLink { get; set; }

		[JsonPropertyName("writeLink")]
		public string? WriteLink { get; set; }
	}
}
