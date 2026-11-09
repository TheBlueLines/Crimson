using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Crimson.Repository;

public sealed record CloudSaveRemoteFile(string? ReadLink, string? WriteLink);

public interface ICloudSaveRepository
{
	/// <summary>Lists every cloud file of a game, keyed by its cloud path.</summary>
	Task<IReadOnlyDictionary<string, CloudSaveRemoteFile>> ListFilesAsync(
		string accountId,
		string appName,
		CancellationToken cancellationToken);

	/// <summary>Registers files for upload and returns where each one must be written.</summary>
	Task<IReadOnlyDictionary<string, CloudSaveRemoteFile>> CreateFilesAsync(
		string accountId,
		string appName,
		IReadOnlyList<string> remotePaths,
		CancellationToken cancellationToken);

	Task<byte[]> DownloadAsync(string readLink, CancellationToken cancellationToken);

	Task UploadFileAsync(string writeLink, string localFile, CancellationToken cancellationToken);
}
