using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Serilog;

namespace Crimson.Core;

public enum CloudSaveOutcome
{
	NotSupported,
	NoSaves,
	UpToDate,
	Uploaded,
	Downloaded,
	Conflict,
	GameRunning,
	Failed
}

public enum CloudSaveResolution
{
	Automatic,
	KeepLocal,
	UseCloud
}

internal enum CloudSaveAction
{
	None,
	NoSaves,
	Upload,
	Download,
	Conflict
}

internal sealed record CloudSaveBaseline(string ManifestName, string? LocalFingerprint);

public sealed record CloudSaveResult(CloudSaveOutcome Outcome, string Message);

/// <summary>
/// Synchronizes a game's save folder with Epic's cloud storage. A sync only acts on its own when
/// exactly one side changed since the last sync. When both changed, or Crimson has no record of
/// an earlier sync and the contents differ, the player decides. Files are backed up before a
/// download replaces them, and a download is fully verified before any local file changes.
/// </summary>
public sealed class CloudSaveManager
{
	private const string BuildVersionFormat = "yyyy.MM.dd-HH.mm.ss";
	private const string ManifestFolder = "manifests/";
	private const int RetainedBackups = 5;
	private const int MaximumTransferConcurrency = 4;
	private static readonly TimeSpan PreLaunchLimit = TimeSpan.FromMinutes(2);
	private static readonly TimeSpan RunningGamePollInterval = TimeSpan.FromSeconds(10);

	private readonly ILogger _log;
	private readonly ICloudSaveRepository _repository;
	private readonly LibraryManager _library;
	private readonly SettingsManager _settings;
	private readonly Func<Task<string?>> _getAccountId;
	private readonly string _root;
	private readonly CloudSaveKnownFolders _folders;
	private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, CloudSaveResult> _lastResults = new(StringComparer.Ordinal);
	private readonly object _stateLock = new();

	public event Action<string, CloudSaveResult>? SyncCompleted;

	public CloudSaveManager(
		ILogger log,
		ICloudSaveRepository repository,
		LibraryManager library,
		SettingsManager settings,
		Func<Task<string?>> getAccountId,
		string dataDirectory)
		: this(log, repository, library, settings, getAccountId, dataDirectory, CloudSaveKnownFolders.Current())
	{
	}

	internal CloudSaveManager(
		ILogger log,
		ICloudSaveRepository repository,
		LibraryManager library,
		SettingsManager settings,
		Func<Task<string?>> getAccountId,
		string dataDirectory,
		CloudSaveKnownFolders folders)
	{
		_log = log;
		_repository = repository;
		_library = library;
		_settings = settings;
		_getAccountId = getAccountId;
		_root = Path.GetFullPath(dataDirectory);
		_folders = folders;
		_library.BeforeLaunch = BeforeLaunchAsync;
		_library.AppExited += game => _ = SyncAfterExitAsync(game);
	}

	public bool IsSupported(Game? game) =>
		game is not null && CloudSaveLocation.TryResolve(game, "0", _folders) is not null;

	public CloudSaveResult? GetLastResult(string appName) => _lastResults.GetValueOrDefault(appName);

	public async Task<CloudSaveResult> SyncAsync(
		string appName,
		CloudSaveResolution resolution = CloudSaveResolution.Automatic,
		CancellationToken cancellationToken = default)
	{
		CloudSaveResult result;
		var gate = GetGate(appName);
		var entered = false;
		try
		{
			await gate.WaitAsync(cancellationToken);
			entered = true;
			result = await Task.Run(() => SyncCoreAsync(appName, resolution, cancellationToken), cancellationToken);
		}
		catch (OperationCanceledException)
		{
			_log.Warning("Cloud save sync timed out or was cancelled for {AppName}", appName);
			result = new CloudSaveResult(CloudSaveOutcome.Failed, "Cloud save sync was cancelled before it finished.");
		}
		catch (Exception ex)
		{
			_log.Error(ex, "Cloud save sync failed for {AppName}", appName);
			result = new CloudSaveResult(CloudSaveOutcome.Failed, "Cloud saves could not be synced. Your local saves were kept.");
		}
		finally
		{
			if (entered)
				gate.Release();
		}

		_lastResults[appName] = result;
		SyncCompleted?.Invoke(appName, result);
		return result;
	}

	internal static CloudSaveAction Decide(
		string? localFingerprint,
		string? remoteManifest,
		CloudSaveBaseline? baseline,
		bool contentMatches)
	{
		if (localFingerprint is null && remoteManifest is null)
			return CloudSaveAction.NoSaves;
		if (remoteManifest is null)
			return CloudSaveAction.Upload;

		if (baseline is null)
		{
			if (localFingerprint is null)
				return CloudSaveAction.Download;
			return contentMatches ? CloudSaveAction.None : CloudSaveAction.Conflict;
		}

		var localChanged = localFingerprint != baseline.LocalFingerprint;
		var remoteChanged = remoteManifest != baseline.ManifestName;
		if (!localChanged && !remoteChanged)
			return CloudSaveAction.None;
		if (localFingerprint is null)
			return CloudSaveAction.Download;
		if (localChanged && remoteChanged)
			return CloudSaveAction.Conflict;
		return localChanged ? CloudSaveAction.Upload : CloudSaveAction.Download;
	}

	private async Task BeforeLaunchAsync(Game game)
	{
		using var timeout = new CancellationTokenSource(PreLaunchLimit);
		if (_settings.CloudSaveAutoSync)
		{
			await SyncAsync(game.AppName, CloudSaveResolution.Automatic, timeout.Token);
			return;
		}

		// A manual sync may be replacing files. The game must not start in the middle of that.
		var gate = GetGate(game.AppName);
		await gate.WaitAsync(timeout.Token);
		gate.Release();
	}

	private async Task SyncAfterExitAsync(Game game)
	{
		try
		{
			if (!_settings.CloudSaveAutoSync)
				return;

			// Some titles exit their bootstrapper while the real game keeps running.
			while (IsInstallFolderBusy(game.LocalAppState?.InstallPath))
				await Task.Delay(RunningGamePollInterval);

			await SyncAsync(game.AppName);
		}
		catch (Exception ex)
		{
			_log.Error(ex, "Post-exit cloud save sync failed for {AppName}", game.AppName);
		}
	}

	private static bool IsInstallFolderBusy(string? installPath)
	{
		var processes = GameProcesses.Find(-1, installPath);
		foreach (var process in processes)
			process.Dispose();
		return processes.Count > 0;
	}

	private SemaphoreSlim GetGate(string appName) => _gates.GetOrAdd(appName, _ => new SemaphoreSlim(1, 1));

	private async Task<CloudSaveResult> SyncCoreAsync(
		string appName,
		CloudSaveResolution resolution,
		CancellationToken cancellationToken)
	{
		var game = _library.GetGameInfo(appName);
		var accountId = await _getAccountId();
		if (game is null || string.IsNullOrWhiteSpace(accountId))
			return new CloudSaveResult(CloudSaveOutcome.NotSupported, "Cloud saves are not available for this game.");

		var location = CloudSaveLocation.TryResolve(game, accountId, _folders);
		if (location is null)
			return new CloudSaveResult(CloudSaveOutcome.NotSupported, "Cloud saves are not available for this game.");
		if (_library.IsAppRunning(appName))
			return new CloudSaveResult(CloudSaveOutcome.GameRunning, "Close the game before syncing its cloud saves.");

		var local = ScanFolder(location);
		var localFingerprint = local.Count == 0 ? null : CloudSaveFingerprint.Compute(local.Select(file => (file.Relative, file.Sha1)));
		var remoteFiles = await _repository.ListFilesAsync(accountId, appName, cancellationToken);
		var manifestKey = FindLatestManifest(remoteFiles.Keys);
		var manifestName = manifestKey is null ? null : FileNameOf(manifestKey);
		var baseline = GetBaseline(accountId, appName);

		var contentMatches = false;
		if (resolution == CloudSaveResolution.Automatic && baseline is null && localFingerprint is not null && manifestKey is not null)
		{
			var manifest = await ReadManifestAsync(remoteFiles, manifestKey, cancellationToken);
			contentMatches = localFingerprint == FingerprintOf(manifest, location.Filter);
		}

		var action = resolution switch
		{
			CloudSaveResolution.KeepLocal when localFingerprint is null => throw new InvalidOperationException("There are no local saves to upload."),
			CloudSaveResolution.UseCloud when manifestKey is null => throw new InvalidOperationException("There are no cloud saves to download."),
			CloudSaveResolution.KeepLocal => CloudSaveAction.Upload,
			CloudSaveResolution.UseCloud => CloudSaveAction.Download,
			_ => Decide(localFingerprint, manifestName, baseline, contentMatches)
		};

		switch (action)
		{
			case CloudSaveAction.Upload:
				await UploadAsync(accountId, appName, location, local, manifestName, cancellationToken);
				return new CloudSaveResult(CloudSaveOutcome.Uploaded, "Your saves were uploaded to the cloud.");
			case CloudSaveAction.Download:
				await DownloadAsync(accountId, appName, location, local, remoteFiles, manifestKey!, cancellationToken);
				return new CloudSaveResult(CloudSaveOutcome.Downloaded, "Your saves were updated from the cloud.");
			case CloudSaveAction.Conflict:
				return new CloudSaveResult(CloudSaveOutcome.Conflict, "Your local and cloud saves are different. Choose which ones to keep.");
			case CloudSaveAction.NoSaves:
				return new CloudSaveResult(CloudSaveOutcome.NoSaves, "No local or cloud saves were found.");
			default:
				if (baseline is null)
					SetBaseline(accountId, appName, new CloudSaveBaseline(manifestName!, localFingerprint));
				return new CloudSaveResult(CloudSaveOutcome.UpToDate, "Your cloud saves are up to date.");
		}
	}

	private async Task UploadAsync(
		string accountId,
		string appName,
		CloudSaveLocation location,
		IReadOnlyList<LocalSaveFile> local,
		string? latestManifest,
		CancellationToken cancellationToken)
	{
		var temp = NewWorkDirectory();
		try
		{
			var package = CloudSavePackager.Package(
				location.Folder,
				local.Select(file => file.Relative),
				appName,
				accountId,
				location.Template,
				NextBuildVersion(latestManifest),
				temp);
			var uploads = package.Chunks.Append(package.Manifest).ToList();
			var links = await _repository.CreateFilesAsync(
				accountId,
				appName,
				uploads.Select(file => file.RemotePath).ToList(),
				cancellationToken);

			await Parallel.ForEachAsync(
				package.Chunks,
				new ParallelOptions { MaxDegreeOfParallelism = MaximumTransferConcurrency, CancellationToken = cancellationToken },
				async (file, token) => await _repository.UploadFileAsync(RequireWriteLink(links, file.RemotePath), file.TempFile, token));

			// The manifest goes last: a partial upload then never becomes the newest save.
			await _repository.UploadFileAsync(RequireWriteLink(links, package.Manifest.RemotePath), package.Manifest.TempFile, cancellationToken);
			SetBaseline(accountId, appName, new CloudSaveBaseline(package.ManifestName, package.Fingerprint));
			_log.Information("Uploaded cloud saves for {AppName}", appName);
		}
		finally
		{
			DeleteDirectory(temp);
		}
	}

	private async Task DownloadAsync(
		string accountId,
		string appName,
		CloudSaveLocation location,
		IReadOnlyList<LocalSaveFile> local,
		IReadOnlyDictionary<string, CloudSaveRemoteFile> remoteFiles,
		string manifestKey,
		CancellationToken cancellationToken)
	{
		var manifest = await ReadManifestAsync(remoteFiles, manifestKey, cancellationToken);
		if (manifest.FileManifestList.Elements.Count == 0)
			throw new InvalidDataException("The cloud save manifest does not contain any files.");

		var work = NewWorkDirectory();
		try
		{
			var staged = await StageFilesAsync(manifest, remoteFiles, manifestKey, location, work, cancellationToken);

			// Everything is verified on disk from here on, so a cancelled sync can no longer interrupt it.
			ApplyDownload(appName, location, local, staged);
			var updated = ScanFolder(location);
			var fingerprint = updated.Count == 0 ? null : CloudSaveFingerprint.Compute(updated.Select(file => (file.Relative, file.Sha1)));
			SetBaseline(accountId, appName, new CloudSaveBaseline(FileNameOf(manifestKey), fingerprint));
			_log.Information("Downloaded cloud saves for {AppName}", appName);
		}
		finally
		{
			DeleteDirectory(work);
		}
	}

	private async Task<IReadOnlyList<StagedFile>> StageFilesAsync(
		Manifest manifest,
		IReadOnlyDictionary<string, CloudSaveRemoteFile> remoteFiles,
		string manifestKey,
		CloudSaveLocation location,
		string work,
		CancellationToken cancellationToken)
	{
		var prefix = manifestKey[..manifestKey.LastIndexOf(ManifestFolder, StringComparison.Ordinal)];
		var chunkDirectory = Directory.CreateDirectory(Path.Combine(work, "chunks")).FullName;
		var chunks = manifest.CDL.Elements.ToDictionary(chunk => chunk.GuidNum);
		await Parallel.ForEachAsync(
			chunks.Values,
			new ParallelOptions { MaxDegreeOfParallelism = MaximumTransferConcurrency, CancellationToken = cancellationToken },
			async (info, token) =>
			{
				var bytes = await _repository.DownloadAsync(RequireReadLink(remoteFiles, prefix + info.Path), token);
				var chunk = Chunk.ReadBuffer(bytes);
				chunk.ValidateAgainst(info);
				await File.WriteAllBytesAsync(Path.Combine(chunkDirectory, info.GuidStr), chunk.Data, token);
			});

		var fileDirectory = Directory.CreateDirectory(Path.Combine(work, "files")).FullName;
		var staged = new List<StagedFile>();
		byte[]? cachedData = null;
		var cachedGuid = string.Empty;
		foreach (var file in manifest.FileManifestList.Elements)
		{
			var target = InstallPathPolicy.ResolveFile(location.Folder, file.Filename);
			var stagedPath = Path.Combine(fileDirectory, staged.Count.ToString(CultureInfo.InvariantCulture));
			using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
			await using (var output = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write))
			{
				foreach (var part in file.ChunkParts)
				{
					cancellationToken.ThrowIfCancellationRequested();
					if (!chunks.ContainsKey(part.GuidNum))
						throw new InvalidDataException("A cloud save file references an unknown chunk.");
					if (part.GuidStr != cachedGuid)
					{
						cachedData = await File.ReadAllBytesAsync(Path.Combine(chunkDirectory, part.GuidStr), cancellationToken);
						cachedGuid = part.GuidStr;
					}

					if ((long)part.Offset + part.Size > cachedData!.Length)
						throw new InvalidDataException("A cloud save file part is outside its chunk.");
					hash.AppendData(cachedData, part.Offset, part.Size);
					await output.WriteAsync(cachedData.AsMemory(part.Offset, part.Size), cancellationToken);
				}

				if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), file.Hash))
					throw new InvalidDataException("A cloud save file failed its integrity check.");
			}

			staged.Add(new StagedFile(stagedPath, target));
		}

		return staged;
	}

	private void ApplyDownload(
		string appName,
		CloudSaveLocation location,
		IReadOnlyList<LocalSaveFile> local,
		IReadOnlyList<StagedFile> staged)
	{
		var comparer = StringComparer.OrdinalIgnoreCase;
		var targets = staged.Select(file => file.Target).ToHashSet(comparer);
		var stale = local.Select(file => file.FullPath).Where(path => !targets.Contains(path)).ToList();
		var overwritten = targets.Where(File.Exists).Concat(stale).Distinct(comparer).ToList();
		var backup = CreateBackup(appName, location.Folder, overwritten);
		try
		{
			foreach (var file in staged)
			{
				Directory.CreateDirectory(Path.GetDirectoryName(file.Target)!);
				if (File.Exists(file.Target))
					File.SetAttributes(file.Target, FileAttributes.Normal);
				File.Move(file.StagedPath, file.Target, overwrite: true);
			}

			foreach (var path in stale)
			{
				File.SetAttributes(path, FileAttributes.Normal);
				File.Delete(path);
			}
		}
		catch
		{
			RestoreBackup(backup, location.Folder);
			throw;
		}
	}

	private string? CreateBackup(string appName, string folder, IReadOnlyList<string> files)
	{
		if (files.Count == 0)
			return null;

		var gameBackups = Path.Combine(_root, "backups", Uri.EscapeDataString(appName));
		var backup = Path.Combine(gameBackups, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
		foreach (var file in files)
		{
			var destination = Path.Combine(backup, Path.GetRelativePath(folder, file));
			Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
			File.Copy(file, destination, overwrite: true);
		}

		foreach (var old in Directory.GetDirectories(gameBackups).OrderDescending(StringComparer.Ordinal).Skip(RetainedBackups))
			DeleteDirectory(old);
		return backup;
	}

	private void RestoreBackup(string? backup, string folder)
	{
		if (backup is null)
			return;

		foreach (var file in Directory.EnumerateFiles(backup, "*", SearchOption.AllDirectories))
		{
			try
			{
				var destination = Path.Combine(folder, Path.GetRelativePath(backup, file));
				Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
				File.Copy(file, destination, overwrite: true);
			}
			catch (Exception ex)
			{
				_log.Error(ex, "Could not restore a save file from backup {Backup}", backup);
			}
		}
	}

	private async Task<Manifest> ReadManifestAsync(
		IReadOnlyDictionary<string, CloudSaveRemoteFile> remoteFiles,
		string manifestKey,
		CancellationToken cancellationToken)
	{
		var bytes = await _repository.DownloadAsync(RequireReadLink(remoteFiles, manifestKey), cancellationToken);
		return Manifest.ReadAll(bytes);
	}

	private static string FingerprintOf(Manifest manifest, CloudSaveFilter filter) => CloudSaveFingerprint.Compute(
		manifest.FileManifestList.Elements
			.Where(file => filter.Includes(file.Filename))
			.Select(file => (file.Filename, file.Hash)));

	private static string? FindLatestManifest(IEnumerable<string> keys) => keys
		.Where(key => key.EndsWith(".manifest", StringComparison.Ordinal) &&
			(key.StartsWith(ManifestFolder, StringComparison.Ordinal) || key.Contains('/' + ManifestFolder, StringComparison.Ordinal)))
		.OrderBy(FileNameOf, StringComparer.Ordinal)
		.LastOrDefault();

	private static string FileNameOf(string key) => key[(key.LastIndexOf('/') + 1)..];

	/// <summary>The newest manifest name wins, so a new one must sort after the current one even with clock skew.</summary>
	private static string NextBuildVersion(string? latestManifest)
	{
		var version = DateTime.UtcNow;
		if (latestManifest is not null && DateTime.TryParseExact(
				latestManifest[..^".manifest".Length],
				BuildVersionFormat,
				CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
				out var latest) && latest >= version)
			version = latest.AddSeconds(1);
		return version.ToString(BuildVersionFormat, CultureInfo.InvariantCulture);
	}

	private static string RequireReadLink(IReadOnlyDictionary<string, CloudSaveRemoteFile> files, string key) =>
		files.TryGetValue(key, out var file) && !string.IsNullOrWhiteSpace(file.ReadLink)
			? file.ReadLink
			: throw new InvalidDataException("The cloud save is missing a file it references.");

	private static string RequireWriteLink(IReadOnlyDictionary<string, CloudSaveRemoteFile> links, string remotePath)
	{
		var suffix = "/" + remotePath;
		var link = links.TryGetValue(remotePath, out var exact)
			? exact.WriteLink
			: links.FirstOrDefault(entry => entry.Key.EndsWith(suffix, StringComparison.Ordinal)).Value?.WriteLink;
		return !string.IsNullOrWhiteSpace(link)
			? link
			: throw new InvalidDataException("Epic did not provide an upload location for a save file.");
	}

	private static List<LocalSaveFile> ScanFolder(CloudSaveLocation location)
	{
		if (!Directory.Exists(location.Folder))
			return [];

		var options = new EnumerationOptions
		{
			RecurseSubdirectories = true,
			AttributesToSkip = FileAttributes.ReparsePoint
		};
		return Directory.EnumerateFiles(location.Folder, "*", options)
			.Select(path => (Path: path, Relative: Path.GetRelativePath(location.Folder, path).Replace('\\', '/')))
			.Where(file => location.Filter.Includes(file.Relative))
			.Select(file =>
			{
				using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
				return new LocalSaveFile(file.Relative, file.Path, SHA1.HashData(stream));
			})
			.ToList();
	}

	private CloudSaveBaseline? GetBaseline(string accountId, string appName)
	{
		lock (_stateLock)
			return LoadState(accountId).GetValueOrDefault(appName);
	}

	private void SetBaseline(string accountId, string appName, CloudSaveBaseline baseline)
	{
		lock (_stateLock)
		{
			var state = LoadState(accountId);
			state[appName] = baseline;
			var path = StatePath(accountId);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			var temp = path + ".tmp";
			File.WriteAllText(temp, JsonSerializer.Serialize(state));
			File.Move(temp, path, overwrite: true);
		}
	}

	private Dictionary<string, CloudSaveBaseline> LoadState(string accountId)
	{
		var path = StatePath(accountId);
		if (!File.Exists(path))
			return new Dictionary<string, CloudSaveBaseline>(StringComparer.Ordinal);

		try
		{
			return JsonSerializer.Deserialize<Dictionary<string, CloudSaveBaseline>>(File.ReadAllText(path))
				?? new Dictionary<string, CloudSaveBaseline>(StringComparer.Ordinal);
		}
		catch (JsonException ex)
		{
			// Without a baseline a sync can only ask the player, so nothing is overwritten.
			_log.Warning(ex, "Ignoring an unreadable cloud save state file");
			return new Dictionary<string, CloudSaveBaseline>(StringComparer.Ordinal);
		}
	}

	private string StatePath(string accountId) =>
		Path.Combine(_root, "accounts", Uri.EscapeDataString(accountId), "state.json");

	private string NewWorkDirectory() => Directory.CreateDirectory(Path.Combine(_root, "work", Guid.NewGuid().ToString("N"))).FullName;

	private void DeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path))
				Directory.Delete(path, recursive: true);
		}
		catch (Exception ex)
		{
			_log.Warning(ex, "Could not remove temporary cloud save data");
		}
	}

	private sealed record LocalSaveFile(string Relative, string FullPath, byte[] Sha1);

	private sealed record StagedFile(string StagedPath, string Target);
}
