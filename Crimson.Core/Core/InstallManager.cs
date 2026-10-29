using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Serilog;

namespace Crimson.Core;

public partial class InstallManager
{
	public event Action<InstallItem> InstallationStatusChanged;
	public event Action<InstallItem> InstallProgressUpdate;

	private readonly ILogger _logger;
	private readonly LibraryManager _libraryManager;
	private readonly DownloadManager _downloadManager;
	private readonly IGameShortcutManager _shortcutManager;
	private readonly IInstallPermissionChecker _permissionChecker;
	private readonly IStoreRepository _repository;
	private readonly Storage _storage;

	private readonly List<InstallItem> _installQueue = [];
	private readonly List<InstallItem> _installHistory = [];
	// Installs that were pushed off the pipeline (by preemption or startup restore). They keep
	// a saved snapshot so they can be resumed later, but no download/IO workers are running.
	private readonly List<InstallItem> _suspendedInstalls = [];
	private readonly Dictionary<string, InstallManagerState> _suspendedStates = new(StringComparer.Ordinal);
	private readonly SemaphoreSlim _pipelineChangeLock = new(1, 1);
	private readonly SemaphoreSlim _restoreGate = new(1, 1);
	private volatile bool _pendingRestoreInProgress;
	// Cancels the file check of a restore in progress, so a preemption does not have to wait for
	// it. _pendingPreemptions covers a preemption that arrives before the check has started.
	private volatile CancellationTokenSource? _restoreCheckCts;
	private int _pendingPreemptions;
	private volatile bool _suspensionRequested;
	private TaskCompletionSource? _suspensionCompletion;

	private readonly ConcurrentDictionary<string, object> _fileLocksConcurrentDictionary = new();
	// Shared chunks are referenced by more than one file part; decompress them once and
	// reuse the payload until the last part has been written.
	private readonly ConcurrentDictionary<BigInteger, Chunk> _chunkCache = new();
	private const int MaxCachedChunks = 32;
	private ConcurrentDictionary<BigInteger, List<FileManifest>> _chunkToFileManifestsDictionary = new();
	private ConcurrentDictionary<BigInteger, int> _chunkPartReferences = new();
	private readonly ConcurrentDictionary<string, byte> _ioQueueTaskSet = new();
	private readonly JsonSerializerOptions _jsonSerializerOptions;

	private readonly object _installItemLock = new();
	private readonly int _numberOfThreads;
	private const int _progressUpdateIntervalInMS = 1000;
	private const int _checkpointIntervalInMS = 30_000;
	private List<FileManifest> _importVerificationResult;

	private BlockingCollection<DownloadTask> _downloadQueue = [];
	private BlockingCollection<IoTask> _ioQueue = [];
	// Progress ledger of the current plan; persisted with the snapshot (see InstallManagerState).
	// _plannedFiles and _partsRemaining are keyed by destination path, the ledger by manifest name.
	private ConcurrentDictionary<string, CompletedFileRecord> _completedFiles = new(StringComparer.Ordinal);
	private ConcurrentDictionary<string, PartialFileProgress> _writtenParts = new(StringComparer.Ordinal);
	private ConcurrentDictionary<string, FileManifest> _plannedFiles = new(StringComparer.OrdinalIgnoreCase);
	private ConcurrentDictionary<string, int> _partsRemaining = new(StringComparer.OrdinalIgnoreCase);
	// Snapshot being restored; while set, planning resumes from its ledger instead of from zero.
	private InstallManagerState? _restoringFrom;
	private List<Task> _downloadTasks;
	private List<Task> _installTasks;
	private CancellationTokenSource _cancellationTokenSource = new();
	private Stopwatch _installStopWatch = new();
	// Progress carried in from earlier sessions; speeds only count bytes moved since the start.
	private double _sessionStartDownloadedMiB;
	private double _sessionStartWrittenMiB;
	private long _lastUpdateTick = long.MinValue / 2;
	private long _lastCheckpointTick = long.MinValue / 2;
	private ManualResetEventSlim _pauseEvent = new ManualResetEventSlim(true);
	private volatile bool _userCancelled;
	private bool _isFinalizing;
	private string? _stoppageReason;

	public InstallItem? CurrentInstall { get; private set; }

	public InstallManager(
		ILogger logger,
		LibraryManager libraryManager,
		IStoreRepository repository,
		Storage storage,
		DownloadManager downloadManager,
		IGameShortcutManager shortcutManager,
		IInstallPermissionChecker permissionChecker)
	{
		_logger = logger;
		_libraryManager = libraryManager;
		_downloadManager = downloadManager;
		_shortcutManager = shortcutManager;
		_permissionChecker = permissionChecker;
		CurrentInstall = null;
		_repository = repository;
		_storage = storage;

		_numberOfThreads = 12;
		_jsonSerializerOptions = new JsonSerializerOptions
		{
			Converters = { new BigIntegerJsonConverter() }
		};
	}

	/// <summary>
	/// Adds game to the install queue.
	/// Starts processing it immediately if no other game is in the queue
	/// </summary>
	/// <param name="item"></param>
	public void AddToQueue(InstallItem item)
	{
		if (item == null)
			return;

		// check if the game is already in the queue
		if (_installQueue.Contains(item, new InstallItemComparer()))
		{
			_logger.Warning("AddToQueue: Game {Name} already in queue", item.AppName);
			return;
		}

		// Check if the game we are trying to install exists in the library
		var gameData = _libraryManager.GetGameInfo(item.AppName);
		if (gameData == null)
		{
			_logger.Warning("AddToQueue: Game {Name} not found in library", item.AppName);
			return;
		}

		if (item.Action == ActionType.ModifyContent)
		{
			if (!CanManageContent(item.AppName))
				return;
			item.ContentOwnerAppName = item.AppName;
			item.Location = gameData.LocalAppState!.InstallPath;
		}

		if (item.Action != ActionType.Install && item.Action != ActionType.Import &&
			(gameData.LocalAppState == null || gameData.LocalAppState?.InstallStatus == InstallState.NotInstalled))
		{
			_logger.Warning("AddToQueue: {AppName} is not installed, cannot {Action}", item.AppName, item.Action);
			return;
		}

		if (item.Action != ActionType.Repair && item.Action != ActionType.Uninstall && gameData.LocalAppState?.InstallStatus == InstallState.Broken)
		{
			_logger.Warning("AddToQueue: {AppName} is broken, forcing repair", item.AppName);
			item.Action = ActionType.Repair;
		}

		_logger.Information("AddToQueue: Adding new Install to queue {Name} Action {Action}", item.AppName, item.Action);

		// A game that was pushed into the paused list resumes rather than queueing a second install.
		if (IsSuspended(item.AppName))
		{
			_logger.Information("AddToQueue: {Name} is paused; resuming it instead of queueing", item.AppName);
			_ = ResumeSuspendedInstallAsync(item.AppName);
			return;
		}

		_installQueue.Add(item);
		InstallationStatusChanged?.Invoke(item);
		if (CurrentInstall == null)
		{
			ProcessNext();
			return;
		}

		// Something else holds the download pipeline: pause it and start the requested game now.
		_ = PreemptCurrentInstallAsync(resumeAppName: null);
	}

	private async void ProcessNext(bool isResuming = false)
	{
		try
		{
			if (isResuming == false && (CurrentInstall != null || _installQueue.Count <= 0)) return;

			_cancellationTokenSource = new CancellationTokenSource();
			_userCancelled = false;
			_stoppageReason = null;
			_suspensionRequested = false;
			lock (_installItemLock)
				_isFinalizing = false;

			if (!isResuming)
			{
				await PrepareTasks();
			}

			if (CurrentInstall == null) return;
			if (_cancellationTokenSource.IsCancellationRequested)
			{
				await HandleStoppageOrSuspensionAsync(Volatile.Read(ref _stoppageReason) ?? "Cancel install");
				return;
			}

			CurrentInstall.Status = ActionStatus.Processing;
			InstallationStatusChanged?.Invoke(CurrentInstall);

			if (CurrentInstall.Action == ActionType.Import || CurrentInstall.Action == ActionType.Move)
			{
				await UpdateInstalledGameStatus();
				if (CurrentInstall != null && _cancellationTokenSource.IsCancellationRequested)
					await HandleStoppageOrSuspensionAsync(Volatile.Read(ref _stoppageReason) ?? "Cancel install");
				return;
			}

			lock (_installItemLock)
			{
				_sessionStartDownloadedMiB = CurrentInstall.DownloadedSizeMiB;
				_sessionStartWrittenMiB = CurrentInstall.WrittenSizeMiB;
			}
			_installStopWatch.Reset();
			_installStopWatch.Start();
			_pauseEvent.Set();

			_downloadTasks = Enumerable.Range(0, _numberOfThreads)
				.Select(_ => Task.Run(ProcessDownloadQueue, _cancellationTokenSource.Token))
				.ToList();

			_installTasks = Enumerable.Range(0, _numberOfThreads)
				.Select(_ => Task.Run(ProcessIOQueue, _cancellationTokenSource.Token))
				.ToList();

			_downloadQueue.CompleteAdding();

			await Task.WhenAll(_downloadTasks);
			_ioQueue.CompleteAdding();
			await Task.WhenAll(_installTasks);

			if (_cancellationTokenSource.IsCancellationRequested)
			{
				await HandleStoppageOrSuspensionAsync(Volatile.Read(ref _stoppageReason) ?? "Cancel install");
				return;
			}

			await UpdateInstalledGameStatus();

		}
		catch (Exception ex)
		{
			_logger.Error(ex, "ProcessNext failed");
			if (_cancellationTokenSource.IsCancellationRequested && _suspensionRequested && !_userCancelled)
				HandleSuspension();
			else
				await HandleInstallationStoppage($"Installation failed: {ex.Message}");
		}
	}

	private async Task PrepareTasks(bool isResuming = false)
	{
		try
		{
			if (!isResuming)
			{
				CurrentInstall = _installQueue[0];
				_installQueue.RemoveAt(0);
				// An install whose restore failed comes back through the queue; plan it from its
				// snapshot so the progress it recorded is kept.
				lock (_installItemLock)
				{
					if (_restoringFrom is null &&
						_suspendedStates.TryGetValue(CurrentInstall.AppName, out var saved) &&
						ReferenceEquals(saved.CurrentInstall, CurrentInstall))
						_restoringFrom = saved;
				}
			}

			var currentInstall = CurrentInstall;
			if (currentInstall == null) return;
			_logger.Information("ProcessNext: Processing {Action} of {AppName}. Game Location {Location} ",
				currentInstall.Action, currentInstall.AppName, currentInstall.Location);

			ValidateContentAction(currentInstall);
			var manifestData = currentInstall.Action == ActionType.ModifyContent ||
				(currentInstall.Action == ActionType.Uninstall && currentInstall.ContentOwnerAppName is not null)
				? await GetInstalledManifestBytes(currentInstall.AppName)
				: await GetManifestDataWithCaching(currentInstall.AppName);
			var gameData = _libraryManager.GetGameInfo(currentInstall.AppName);

			_logger.Information("ProcessNext: Parsing game manifest");
			var data = Manifest.ReadAll(manifestData);

			// TODO Handle stats if game is installed


			if (currentInstall.Action == ActionType.Install)
			{
				// create CurrentInstall.folder if it doesn't exist
				if (!Directory.Exists(currentInstall.Location))
				{
					Directory.CreateDirectory(currentInstall.Location);
					_logger.Debug("Folder created at: {location}", currentInstall.Location);
				}
			}

			var permission = _permissionChecker.Check(currentInstall.Location);
			if (!permission.CanWrite)
			{
				_logger.Warning(
					"Install location write check failed with {ErrorType}; cleanup result {CleanupErrorType}",
					permission.ErrorType,
					permission.CleanupErrorType);
				throw new UnauthorizedAccessException("No write permissions to install location");
			}

			ResetQueues();
			if (_restoringFrom is not null)
			{
				currentInstall.TotalDownloadSizeBytes = 0;
				currentInstall.TotalDownloadSizeMiB = 0;
				currentInstall.DownloadedSizeMiB = 0;
				currentInstall.TotalWriteSizeBytes = 0;
				currentInstall.TotalWriteSizeMb = 0;
				currentInstall.WrittenSizeMiB = 0;
			}
			await PrepareTasksForAction(currentInstall, gameData, data);
			if (_restoringFrom is not null)
				UpdateProgressPercentage(currentInstall);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			_logger.Error(ex, "PrepareTasks: Exception occured while preparing tasks");
			throw;
		}
		finally
		{
			if (!isResuming)
				_restoringFrom = null;
		}
	}

	private async Task PrepareTasksForAction(
		InstallItem install,
		Game gameData,
		Manifest data)
	{
		if (install.Action is ActionType.Install or ActionType.Update or ActionType.Repair)
			data = SelectManifest(data, install.SelectedInstallTags ?? gameData.LocalAppState?.SelectedInstallTags);

		switch (install.Action)
		{
			case ActionType.ModifyContent:
				await PrepareContentTasks(install, gameData, data);
				break;
			case ActionType.Install:
				await _downloadManager.InitializeMirrors(gameData.BaseUrls);
				await PlanFilesAsync(data, data.FileManifestList.Elements);
				break;
			case ActionType.Update:
				await PrepareUpdateTasks(gameData, data);
				break;
			case ActionType.Repair:
				await PrepareRepairTasks(gameData, data);
				break;
			case ActionType.Uninstall:
				if (install.ContentOwnerAppName is not null)
					data = await ExcludeSharedContent(install, data);
				PrepareUninstallTasks(install, data);
				break;
			case ActionType.Import:
				PrepareImportTasks(install, data);
				break;
			case ActionType.Move:
				PrepareMoveTasks(install);
				break;
		}
	}

	private void PrepareUninstallTasks(InstallItem install, Manifest data)
	{
		foreach (var file in data.FileManifestList.Elements)
		{
			// Always resolve the path first so traversal attempts are rejected even
			// when the file no longer exists on disk.
			var filePath = InstallPathPolicy.ResolveFile(install.Location, file.Filename);
			if (!File.Exists(filePath))
			{
				_logger.Debug("Uninstall: skipping missing file {Filename}", file.Filename);
				continue;
			}

			install.TotalWriteSizeBytes += file.FileSize;
			install.TotalWriteSizeMb = install.TotalWriteSizeBytes / 1024.0 / 1024.0;
			_ioQueue.Add(new IoTask
			{
				DestinationFilePath = filePath,
				TaskType = IoTaskType.Delete,
				Size = file.FileSize
			});
		}
	}

	private void PrepareImportTasks(InstallItem install, Manifest data)
	{
		if (!Directory.Exists(install.Location))
			throw new DirectoryNotFoundException("Import folder does not exist");

		// Existing imports can differ from the latest manifest; verify hashes separately.
		var missingFiles = new List<FileManifest>();
		foreach (var file in data.FileManifestList.Elements)
		{
			if (!File.Exists(InstallPathPolicy.ResolveFile(install.Location, file.Filename)))
				missingFiles.Add(file);
		}
		_importVerificationResult = missingFiles;
		if (missingFiles.Count == 0)
			_logger.Information("Import: All {Total} files found for {AppName}", data.FileManifestList.Elements.Count, install.AppName);
		else
			_logger.Warning("Import: {Missing}/{Total} files missing for {AppName}. Will import as Broken.",
				missingFiles.Count, data.FileManifestList.Elements.Count, install.AppName);
	}

	private void PrepareMoveTasks(InstallItem install)
	{
		if (!_storage.AreOnSameVolume(install.Location, install.MoveLocation))
			throw new IOException("Cross-volume moves are not supported. Please uninstall and reinstall to the new location.");

		if (Directory.Exists(install.MoveLocation))
			throw new IOException("Destination directory already exists");

		_logger.Information("Move: Moving {AppName} from {Src} to {Dest}",
			install.AppName, install.Location, install.MoveLocation);
		Directory.Move(install.Location, install.MoveLocation);
		_logger.Information("Move: Successfully moved {AppName}", install.AppName);
	}

	private void ResetQueues()
	{
		_downloadQueue.Dispose();
		_ioQueue.Dispose();
		_downloadQueue = [];
		_ioQueue = [];
		_ioQueueTaskSet.Clear();
		_chunkToFileManifestsDictionary = new();
		_chunkPartReferences = new();
		_chunkCache.Clear();
		_completedFiles = new(StringComparer.Ordinal);
		_writtenParts = new(StringComparer.Ordinal);
		_plannedFiles = new(StringComparer.OrdinalIgnoreCase);
		_partsRemaining = new(StringComparer.OrdinalIgnoreCase);
		_fileLocksConcurrentDictionary.Clear();
	}

	private async Task ProcessDownloadQueue()
	{
		try
		{
			foreach (var downloadTask in _downloadQueue.GetConsumingEnumerable(_cancellationTokenSource.Token))
			{
				try
				{

					_pauseEvent.Wait(_cancellationTokenSource.Token);

					var downloaded = await _downloadManager.DownloadFileWithFallback(
						downloadTask.Url,
						downloadTask.TempPath,
						cancellationToken: _cancellationTokenSource.Token,
						expectedSize: downloadTask.ChunkInfo.FileSize,
						expectedChunk: downloadTask.ChunkInfo);
					if (!downloaded)
						throw new IOException(
							$"Failed to download chunk {downloadTask.GuidNum} from all mirrors");

					UpdateDownloadProgress(downloadTask.ChunkInfo.FileSize);
					CreateIoTasksForChunk(downloadTask);
				}
				catch (OperationCanceledException)
				{
					return;
				}
				catch (Exception ex)
				{
					_logger.Error(ex, "ProcessDownloadQueue failed");
					RequestStoppage($"Download failed: {ex.Message}");
					return;
				}
			}
		}
		// only exception happening here wll be the cancellation token being called
		// just handle it not make application crash
		catch (Exception)
		{
			return;
		}
	}

	private void CreateIoTasksForChunk(DownloadTask downloadTask)
	{
		// get file manifest from dictionary
		var fileManifests = _chunkToFileManifestsDictionary[downloadTask.GuidNum];
		foreach (var fileManifest in fileManifests)
		{
			foreach (var part in fileManifest.ChunkParts)
			{
				if (part.GuidNum != downloadTask.GuidNum) continue;

				// mandatory check to prevent duplicate io tasks
				var ioTaskHashString = $"{fileManifest.Filename}.{part.GuidNum}.{part.FileOffset}";
				if (!_ioQueueTaskSet.TryAdd(ioTaskHashString, 0))
					continue;

				var task = new IoTask()
				{
					SourceFilePath = downloadTask.TempPath,
					DestinationFilePath = InstallPathPolicy.ResolveFile(CurrentInstall.Location, fileManifest.Filename),
					TaskType = IoTaskType.Copy,
					Size = part.Size,
					Offset = part.Offset,
					FileOffset = part.FileOffset,
					DestinationFileSize = fileManifest.FileSize,
					GuidNum = part.GuidNum,
					SourceChunkGuidNum = downloadTask.GuidNum
				};
				_ioQueue.Add(task);
			}
		}
	}

	private async Task ProcessIOQueue()
	{
		try
		{
			var install = CurrentInstall ?? throw new InvalidOperationException("No active installation for IO processing.");
			foreach (var ioTask in _ioQueue.GetConsumingEnumerable(_cancellationTokenSource.Token))
			{
				try
				{
					_pauseEvent.Wait(_cancellationTokenSource.Token);
					ValidateContentAction(install);

					switch (ioTask.TaskType)
					{
						case IoTaskType.Copy:
							await ProcessCopyTask(ioTask, install.Location);
							break;
						case IoTaskType.Delete:
							DeleteInstalledFile(install.Location, ioTask.DestinationFilePath);
							break;
					}
					UpdateInstallWriteProgress(ioTask.Size);
				}
				catch (OperationCanceledException)
				{
					return;
				}
				catch (Exception ex)
				{
					_logger.Error(ex, "ProcessIoQueue: IO task failed");
					RequestStoppage($"Installing game files failed: {ex.Message}");
					return;
				}

			}
		}
		// only exception happening here wll be the cancellation token being called
		// just handle it not make application crash
		catch (Exception)
		{
			return;
		}
	}

	private void DeleteInstalledFile(string installRoot, string filePath)
	{
		var path = InstallPathPolicy.RequireFile(installRoot, filePath);
		if (!File.Exists(path))
			return;

		try
		{
			var attributes = File.GetAttributes(path);
			if ((attributes & FileAttributes.ReadOnly) != 0)
				File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
		}
		catch (Exception ex)
		{
			_logger.Warning(ex, "Uninstall: failed to clear read-only attribute on {Path}", path);
		}

		File.Delete(path);
	}

	private async Task ProcessCopyTask(IoTask ioTask, string installRoot)
	{
		InstallPathPolicy.RequireFile(installRoot, ioTask.DestinationFilePath);
		EnsureDirectoryExists(ioTask.DestinationFilePath);

		// Ensure there is a lock object for each destination file
		var fileLock =
			_fileLocksConcurrentDictionary.GetOrAdd(ioTask.DestinationFilePath, new object());

		var chunk = await ReadChunkAsync(ioTask);
		var offset = checked((int)ioTask.Offset);
		var size = checked((int)ioTask.Size);
		if (offset < 0 || size < 0 || offset > chunk.Data.Length - size)
			throw new InvalidDataException("Chunk part is outside the decompressed chunk payload.");

		lock (fileLock)
		{
			var destination = InstallPathPolicy.RequireFile(installRoot, ioTask.DestinationFilePath);
			using var fileStream = new FileStream(destination, FileMode.OpenOrCreate,
			FileAccess.Write, FileShare.None);
			if (ioTask.DestinationFileSize > 0 && fileStream.Length != ioTask.DestinationFileSize)
				fileStream.SetLength(ioTask.DestinationFileSize);

			fileStream.Seek(ioTask.FileOffset, SeekOrigin.Begin);
			fileStream.Write(chunk.Data.AsSpan(offset, size));
			fileStream.Flush();
		}

		// Check for references to the chunk and decrement by one
		int newCount = _chunkPartReferences.AddOrUpdate(
			ioTask.GuidNum,
			(key) => 0, // Not expected to be called as the key should exist
			(key, oldValue) =>
			{
				return oldValue - 1;
			}
		);

		// Check if the updated count is 0 or less
		if (newCount <= 0 && _chunkPartReferences.TryRemove(ioTask.GuidNum, out _))
		{
			_chunkCache.TryRemove(ioTask.SourceChunkGuidNum, out _);
			// Delete the file if successfully removed
			File.Delete(ioTask.SourceFilePath);
		}

		RecordPartWritten(ioTask);
	}

	/// <summary>
	/// Adds a written part to the ledger and records the file as complete once its last part has
	/// landed. The completion record goes in before the part list is dropped, so a snapshot taken
	/// in between still accounts for the file.
	/// </summary>
	private void RecordPartWritten(IoTask ioTask)
	{
		var path = ioTask.DestinationFilePath;
		if (!_plannedFiles.TryGetValue(path, out var file))
			return;

		_writtenParts
			.GetOrAdd(file.Filename, _ => new PartialFileProgress(Convert.ToHexString(file.ShaHash)))
			.FileOffsets.TryAdd(ioTask.FileOffset, 0);

		if (_partsRemaining.AddOrUpdate(path, 0, (_, remaining) => remaining - 1) > 0)
			return;

		_partsRemaining.TryRemove(path, out _);
		_completedFiles[file.Filename] = CompletedFileRecord.From(file, new FileInfo(path));
		_writtenParts.TryRemove(file.Filename, out _);
	}

	private sealed class PartialFileProgress(string sha)
	{
		public string Sha { get; } = sha;
		public ConcurrentDictionary<long, byte> FileOffsets { get; } = new();
	}

	private async Task<Chunk> ReadChunkAsync(IoTask ioTask)
	{
		var shared = _chunkPartReferences.TryGetValue(ioTask.SourceChunkGuidNum, out var references)
			&& references > 1;
		if (!shared)
			return Chunk.ReadBuffer(await File.ReadAllBytesAsync(ioTask.SourceFilePath));

		if (_chunkCache.TryGetValue(ioTask.SourceChunkGuidNum, out var cached))
			return cached;

		var chunk = Chunk.ReadBuffer(await File.ReadAllBytesAsync(ioTask.SourceFilePath));
		return _chunkCache.Count < MaxCachedChunks
			? _chunkCache.GetOrAdd(ioTask.SourceChunkGuidNum, chunk)
			: chunk;
	}

	private void EnsureDirectoryExists(string filePath)
	{

		var directoryPath = Path.GetDirectoryName(filePath);
		if (!string.IsNullOrEmpty(directoryPath))
		{
			Directory.CreateDirectory(directoryPath);
		}
	}

	/// <summary>
	/// Creates or updates installed games list after install completed
	/// </summary>
	/// <exception cref="Exception"></exception>
	/// <exception cref="ArgumentOutOfRangeException"></exception>
	private async Task UpdateInstalledGameStatus()
	{
		var install = CurrentInstall;
		if (install is null)
		{
			_logger.Error("UpdateInstalledGameStatus: Current install is null");
			return;
		}

		try
		{
			if (!IsInstallationInProgress())
				return;

			// All download and IO workers have already completed (ProcessNext awaits them),
			// so no settling delay is needed before finalization.
			_installStopWatch.Reset();
			if (!TryBeginFinalization(install))
			{
				await HandleStoppageOrSuspensionAsync(Volatile.Read(ref _stoppageReason) ?? "Cancel install");
				return;
			}
			var game = GetInstalledGame(install);
			var localAppState = GetLocalAppState(install);
			await ApplyCompletedActionAsync(install, game, localAppState);
			await CreateRequestedShortcutsAsync(install, game, localAppState);
			CompleteInstallation(install);
		}
		catch (OperationCanceledException) when (_userCancelled)
		{
			await HandleInstallationStoppage("Cancel install");
		}
		catch (Exception ex) when (_suspensionRequested && !_userCancelled)
		{
			_logger.Debug(ex, "UpdateInstalledGameStatus interrupted by suspension");
			HandleSuspension();
		}
		catch (Exception ex)
		{
			FailCurrentInstallation(ex);
		}
	}

	private bool TryBeginFinalization(InstallItem install)
	{
		lock (_installItemLock)
		{
			if (!ReferenceEquals(CurrentInstall, install) || _userCancelled || _cancellationTokenSource.IsCancellationRequested)
				return false;

			_isFinalizing = true;
			return true;
		}
	}

	private Game GetInstalledGame(InstallItem install)
	{
		var game = _libraryManager.GetGameInfo(install.AppName);
		if (game is not null)
			return game;

		_logger.Error(
			"UpdateInstalledGameStatus: Found no game data for app name: {AppName}",
			install.AppName);
		throw new Exception("Invalid game data");
	}

	private LocalAppState GetLocalAppState(InstallItem install)
	{
		if (_storage.LocalAppStateDictionary.TryGetValue(install.AppName, out var localAppState))
			return localAppState;
		if (install.Action == ActionType.Import)
			return new LocalAppState { AppName = install.AppName };

		_logger.Error(
			"UpdateInstalledGameStatus: Found no installed game data for {AppName}",
			install.AppName);
		throw new Exception("Invalid installed game data");
	}

	private async Task ApplyCompletedActionAsync(
		InstallItem install,
		Game game,
		LocalAppState localAppState)
	{
		switch (install.Action)
		{
			case ActionType.Uninstall:
				CompleteUninstall(install, game, localAppState);
				break;
			case ActionType.Move:
				CompleteMove(install, game, localAppState);
				break;
			case ActionType.Import:
				await CompleteImportAsync(install, game, localAppState);
				break;
			default:
				await CompleteInstallAsync(install, game, localAppState);
				break;
		}
	}

	private void CompleteUninstall(InstallItem install, Game game, LocalAppState localAppState)
	{
		localAppState.InstallStatus = InstallState.NotInstalled;
		localAppState.InstallPath = null;
		localAppState.Version = null;
		localAppState.Executable = null;
		SaveLocalAppState(game, localAppState);
		TryRemoveShortcuts(game);
		if (install.ContentOwnerAppName is null)
			DeleteInstallDirectory(install, install.Location);
		_logger.Information(
			"UpdateInstalledGameStatus: Uninstall complete for {AppName}",
			install.AppName);
	}

	private void DeleteInstallDirectory(InstallItem install, string? installPath)
	{
		if (string.IsNullOrWhiteSpace(installPath))
			return;

		try
		{
			var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installPath));
			var pathRoot = Path.GetPathRoot(path);
			if (pathRoot is not null &&
				string.Equals(
					Path.TrimEndingDirectorySeparator(pathRoot),
					path,
					OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
			{
				_logger.Error("Uninstall: refusing to delete the volume root {Path}", path);
				return;
			}

			if (!Directory.Exists(path))
				return;

			ClearReadOnlyAttributes(path);
			Directory.Delete(path, true);
		}
		catch (Exception ex)
		{
			_logger.Warning(ex, "Uninstall: failed to delete the install directory {Path}", installPath);
			install.StatusMessage = "The game was removed from your library, but some files could not be deleted.";
		}
	}

	private static void ClearReadOnlyAttributes(string directory)
	{
		foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
		{
			try
			{
				var attributes = File.GetAttributes(file);
				if ((attributes & FileAttributes.ReadOnly) != 0)
					File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
			}
			catch (Exception)
			{
			}
		}
	}

	private void CompleteMove(InstallItem install, Game game, LocalAppState localAppState)
	{
		localAppState.InstallPath = install.MoveLocation;
		SaveLocalAppState(game, localAppState);
		// DLC installed into the game's folder moved along with it.
		foreach (var dlc in _libraryManager.GetDlcsForGame(install.AppName))
		{
			if (_storage.LocalAppStateDictionary.TryGetValue(dlc.AppName, out var dlcState) &&
				!string.IsNullOrWhiteSpace(dlcState.InstallPath) &&
				string.Equals(Path.GetFullPath(dlcState.InstallPath), Path.GetFullPath(install.Location), StringComparison.OrdinalIgnoreCase))
			{
				dlcState.InstallPath = install.MoveLocation;
				SaveLocalAppState(dlc, dlcState);
			}
		}
		_logger.Information(
			"UpdateInstalledGameStatus: Move complete for {AppName}",
			install.AppName);
	}

	private async Task CompleteImportAsync(
		InstallItem install,
		Game game,
		LocalAppState localAppState)
	{
		var manifest = await GetManifestAsync(install, game);
		localAppState.InstallStatus = _importVerificationResult is { Count: > 0 }
			? InstallState.Broken
			: InstallState.Installed;
		ApplyManifestState(install, game, localAppState, manifest);
		SaveLocalAppState(game, localAppState);
		if (localAppState.InstallStatus == InstallState.Installed)
			await RegisterPresentDlcsAsync(install);

		var totalFiles = manifest.FileManifestList.Elements.Count;
		var missingCount = _importVerificationResult?.Count ?? 0;
		install.StatusMessage =
			$"Verified {totalFiles} files: {totalFiles - missingCount} found, {missingCount} missing";
		_importVerificationResult = null;
		_logger.Information(
			"UpdateInstalledGameStatus: Import complete for {AppName}, status: {Status}",
			install.AppName,
			localAppState.InstallStatus);
	}

	/// <summary>
	/// Marks DLC whose files are already in an imported game's folder as installed there,
	/// so content downloaded by another launcher or an earlier Crimson data folder is kept.
	/// </summary>
	private async Task RegisterPresentDlcsAsync(InstallItem install)
	{
		foreach (var dlc in _libraryManager.GetDlcsForGame(install.AppName))
		{
			_storage.LocalAppStateDictionary.TryGetValue(dlc.AppName, out var dlcState);
			if (dlcState?.InstallStatus is not (null or InstallState.NotInstalled) || dlc.AssetInfos?.Windows is null)
				continue;
			try
			{
				var manifest = Manifest.ReadAll(await GetManifestDataWithCaching(dlc.AppName));
				var files = manifest.FileManifestList.Elements;
				// Size checks keep this cheap; a later repair verifies hashes.
				if (files.Count == 0 || !files.All(file =>
					new FileInfo(InstallPathPolicy.ResolveFile(install.Location, file.Filename)) is { Exists: true } info &&
					info.Length == file.FileSize))
					continue;

				_storage.LocalAppStateDictionary.TryGetValue(dlc.AppName, out dlcState);
				dlcState ??= new LocalAppState { AppName = dlc.AppName };
				dlcState.InstallStatus = InstallState.Installed;
				ApplyManifestState(install, dlc, dlcState, manifest);
				SaveLocalAppState(dlc, dlcState);
				_logger.Information("Import: Found installed DLC {AppName} in {Location}", dlc.AppName, install.Location);
			}
			catch (Exception ex)
			{
				_logger.Warning(ex, "Import: Could not check DLC {AppName}", dlc.AppName);
			}
		}
	}

	private async Task CompleteInstallAsync(
		InstallItem install,
		Game game,
		LocalAppState localAppState)
	{
		var manifest = install.Action == ActionType.ModifyContent
			? Manifest.ReadAll(await GetInstalledManifestBytes(install.AppName))
			: await GetManifestAsync(install, game);
		manifest = SelectManifest(manifest, install.SelectedInstallTags ?? localAppState.SelectedInstallTags);
		var invalidFiles = await VerifyFiles(install.Location, manifest.FileManifestList.Elements);
		var previousStatus = localAppState.InstallStatus;
		SetVerificationStatus(install, localAppState, invalidFiles.Count);
		if (install.Action == ActionType.ModifyContent && invalidFiles.Count == 0)
			localAppState.InstallStatus = previousStatus;
		if (invalidFiles.Count == 0 && install.SelectedInstallTags is not null)
			localAppState.SelectedInstallTags = [.. install.SelectedInstallTags];
		if (install.Action != ActionType.ModifyContent)
			ApplyManifestState(install, game, localAppState, manifest);
		SaveLocalAppState(game, localAppState);
		EnsureVerificationSucceeded(install, invalidFiles.Count);
	}

	private async Task<Manifest> GetManifestAsync(InstallItem install, Game game)
	{
		var buildVersion = game.AssetInfos.Windows.BuildVersion;
		var manifestBytes = await _storage.GetCachedManifestBytes(install.AppName, buildVersion);
		var urlData = await _repository.GetManifestUrls(
			game.AssetInfos.Windows.Namespace,
			game.AssetInfos.Windows.CatalogItemId,
			game.AppName);
		if (urlData is null)
		{
			_logger.Error(
				"UpdateInstalledGameStatus: Failed to get manifest urls for {AppName}",
				install.AppName);
			throw new Exception("Cannot fetch manifest data");
		}

		if (manifestBytes is null || manifestBytes.Length < 1)
		{
			manifestBytes = await _repository.GetGameManifest(urlData);
			await _storage.CacheManifestBytes(install.AppName, buildVersion, manifestBytes);
		}

		return Manifest.ReadAll(manifestBytes);
	}

	private void SetVerificationStatus(
		InstallItem install,
		LocalAppState localAppState,
		int invalidFileCount)
	{
		if (invalidFileCount > 0)
		{
			_logger.Warning(
				"UpdateInstalledGameStatus: {Count} files failed verification for {AppName}. Marking as Broken.",
				invalidFileCount,
				install.AppName);
			localAppState.InstallStatus = InstallState.Broken;
			return;
		}

		_logger.Information(
			"UpdateInstalledGameStatus: Verification successful for {AppName}",
			install.AppName);
		localAppState.InstallStatus = InstallState.Installed;
	}

	private static void EnsureVerificationSucceeded(InstallItem install, int invalidFileCount)
	{
		if (invalidFileCount == 0)
			return;

		install.StatusMessage = $"{invalidFileCount} files failed verification";
		throw new InvalidDataException(install.StatusMessage);
	}

	private static void ApplyManifestState(
		InstallItem install,
		Game game,
		LocalAppState localAppState,
		Manifest manifest)
	{
		localAppState.BaseUrls = game.BaseUrls;
		localAppState.CanRunOffline = game.Metadata?.CustomAttributes?.CanRunOffline?.Value == "true";
		localAppState.Executable = manifest.ManifestMeta.LaunchExe;
		localAppState.InstallPath = install.Location;
		localAppState.LaunchParameters = manifest.ManifestMeta.LaunchCommand;
		localAppState.RequiresOt = game.Metadata?.CustomAttributes?.OwnershipToken?.Value == "true";
		localAppState.Version = manifest.ManifestMeta.BuildVersion;
		localAppState.Title = game.AppTitle;

		if (manifest.ManifestMeta.UninstallActionPath is not null)
		{
			localAppState.Uninstaller = new Dictionary<string, string>
			{
				{ manifest.ManifestMeta.UninstallActionPath, manifest.ManifestMeta.UninstallActionArgs }
			};
		}
	}

	private void SaveLocalAppState(Game game, LocalAppState localAppState)
	{
		game.LocalAppState = localAppState;
		_storage.AddToLocalAppState(game.AppName, localAppState);
		_libraryManager.UpdateGameInfo(game);
	}

	private void CompleteInstallation(InstallItem install)
	{
		CleanupTemporaryDirectory(install);
		install.Status = ActionStatus.Success;
		_installHistory.Add(install);
		InstallManagerStateRemoved(install);
		InstallationStatusChanged?.Invoke(install);
		CurrentInstall = null;
		ProcessNext();
	}

	/// <summary>
	/// Drops the persisted (paused) snapshot of an install that finished, failed or was cancelled
	/// so it cannot be restored as paused on the next start.
	/// </summary>
	private void InstallManagerStateRemoved(InstallItem install)
	{
		if (install?.AppName is null)
			return;

		lock (_installItemLock)
			_suspendedStates.Remove(install.AppName);
		_storage.DeleteInstallState(install.AppName);
	}

	/// <summary>
	/// Removes what a cancelled first-time install wrote: every file its manifest names, then the
	/// folders left empty, the install folder included. Anything else in those folders is kept.
	/// Updates, repairs and content changes are left alone since they work on an installed game.
	/// </summary>
	private async Task DeleteCancelledInstallFilesAsync(InstallItem install)
	{
		if (install.Action != ActionType.Install || install.ContentOwnerAppName is not null ||
			string.IsNullOrWhiteSpace(install.Location) || !Directory.Exists(install.Location))
			return;

		var game = _libraryManager.GetGameInfo(install.AppName);
		if (game is null || game.LocalAppState?.InstallStatus is not (null or InstallState.NotInstalled))
			return;

		try
		{
			var tempDirectory = Path.Combine(install.Location, ".Crimson");
			if (Directory.Exists(tempDirectory))
				Directory.Delete(tempDirectory, true);

			var manifest = Manifest.ReadAll(await GetManifestDataWithCaching(install.AppName));
			var deleted = 0;
			foreach (var file in manifest.FileManifestList.Elements)
			{
				var path = InstallPathPolicy.ResolveFile(install.Location, file.Filename);
				try
				{
					if (!File.Exists(path))
						continue;
					File.SetAttributes(path, FileAttributes.Normal);
					File.Delete(path);
					deleted++;
				}
				catch (Exception ex)
				{
					_logger.Warning(ex, "Cancel: failed to delete {Path}", path);
				}
			}

			DeleteEmptyDirectories(Path.TrimEndingDirectorySeparator(Path.GetFullPath(install.Location)));
			_logger.Information("Cancel: removed {Count} downloaded files of {AppName} from {Location}",
				deleted, install.AppName, install.Location);
		}
		catch (Exception ex)
		{
			_logger.Warning(ex, "Cancel: could not remove the downloaded files of {AppName}", install.AppName);
		}
	}

	private static void DeleteEmptyDirectories(string directory)
	{
		// Never follow junctions or symlinks out of the install folder.
		if (new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
			return;

		foreach (var child in Directory.EnumerateDirectories(directory))
			DeleteEmptyDirectories(child);

		var root = Path.GetPathRoot(directory);
		if (!string.Equals(Path.TrimEndingDirectorySeparator(root ?? string.Empty), directory, StringComparison.OrdinalIgnoreCase) &&
			!Directory.EnumerateFileSystemEntries(directory).Any())
			Directory.Delete(directory);
	}

	private void CleanupTemporaryDirectory(InstallItem install)
	{
		if (install.Action is not ActionType.Install and not ActionType.Update and not ActionType.Repair and not ActionType.ModifyContent)
			return;

		var tempDirectory = Path.Combine(install.Location, ".Crimson");
		try
		{
			if (Directory.Exists(tempDirectory))
				Directory.Delete(tempDirectory, true);
		}
		catch (Exception ex)
		{
			_logger.Warning(ex, "Failed to clean up temporary directory {Directory}", tempDirectory);
		}
	}

	private void FailCurrentInstallation(Exception exception)
	{
		_logger.Error(exception, "UpdateInstalledGameStatus failed");
		if (CurrentInstall is not null)
		{
			CancelRemainingContentActions(CurrentInstall);
			CurrentInstall.Status = ActionStatus.Failed;
			CurrentInstall.StatusMessage = string.IsNullOrWhiteSpace(exception.Message)
				? "Installation failed"
				: exception.Message;
			_installHistory.Add(CurrentInstall);
			InstallManagerStateRemoved(CurrentInstall);
			InstallationStatusChanged?.Invoke(CurrentInstall);
		}

		CurrentInstall = null;
		ProcessNext();
	}

	private void TryRemoveShortcuts(Game game)
	{
		try
		{
			_shortcutManager.Remove(game);
		}
		catch (Exception ex)
		{
			_logger.Warning(ex, "Failed to remove shortcuts for {AppName}", game.AppName);
		}
	}

	private async Task CreateRequestedShortcutsAsync(
		InstallItem install,
		Game game,
		LocalAppState localAppState)
	{
		if (install.Action != ActionType.Install || localAppState.InstallStatus != InstallState.Installed || game.IsDlc())
			return;

		_cancellationTokenSource.Token.ThrowIfCancellationRequested();
		if (install.CreateStartMenuShortcut)
			await TryCreateShortcutAsync(game, GameShortcutLocation.StartMenu);
		_cancellationTokenSource.Token.ThrowIfCancellationRequested();
		if (install.CreateDesktopShortcut)
			await TryCreateShortcutAsync(game, GameShortcutLocation.Desktop);
	}

	private async Task TryCreateShortcutAsync(Game game, GameShortcutLocation location)
	{
		var cancellationToken = _cancellationTokenSource.Token;
		try
		{
			await _shortcutManager.CreateAsync(game, location, cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.Warning(ex, "Failed to create {Location} shortcut for {AppName}", location, game.AppName);
		}
	}

	private async Task<List<FileManifest>> VerifyFiles(string installPath, List<FileManifest> fileManifestLists)
	{
		if (!Directory.Exists(CurrentInstall.Location))
		{
			throw new Exception("Invalid installPath provided");
		}
		var options = new ParallelOptions()
		{
			MaxDegreeOfParallelism = Environment.ProcessorCount,
			CancellationToken = RestoreCheckToken
		};

		// Loop through each file in fileManifest
		var invalidFilesBag = new ConcurrentBag<FileManifest>();
		await Parallel.ForEachAsync(fileManifestLists, options, async (manifest, token) =>
			{
				try
				{
					var filePath = InstallPathPolicy.ResolveFile(installPath, manifest.Filename);

					// Check if file exists and add to list if it doesn't
					if (!File.Exists(filePath))
					{
						_logger.Debug("VerifyFiles: Missing {Filename}", manifest.Filename);
						invalidFilesBag.Add(manifest);
						return;
					}

					if (!FileHash.Sha1Matches(filePath, manifest.ShaHash, token))
					{
						// Only build diagnostic strings for the rare mismatch.
						var fileInfo = new FileInfo(filePath);
						_logger.Debug("VerifyFiles: Hash mismatch {Filename} (size={Size}, expected={Expected}, actual={Actual})",
							manifest.Filename, fileInfo.Length,
							Convert.ToHexString(manifest.ShaHash), FileHash.ComputeSha1(filePath));
						invalidFilesBag.Add(manifest);
					}
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					_logger.Error(ex, "VerifyFiles: Error verifying file {Filename}", manifest.Filename);
					invalidFilesBag.Add(manifest);
				}
			});
		return invalidFilesBag.ToList();
	}

	/// <summary>
	/// Plans the download of <paramref name="files"/>: queues every chunk a missing part needs and
	/// wires the chunk-to-file map, part counts and size totals the workers rely on. While a
	/// snapshot is being restored, files and parts its ledger accounts for are skipped.
	/// </summary>
	private async Task PlanFilesAsync(Manifest data, IReadOnlyList<FileManifest> files)
	{
		var done = new HashSet<string>(StringComparer.Ordinal);
		var written = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
		if (_restoringFrom is { } restoring)
			(done, written) = await ResolveRestoredProgressAsync(CurrentInstall, files, restoring);
		BuildPlan(data, files, done, written);
	}

	/// <summary>
	/// Works out which planned files a restored snapshot has already finished and which parts of
	/// the others are written. Ledger entries are trusted while the file is untouched since it was
	/// recorded; only snapshots from before the ledger existed fall back to hashing the disk.
	/// </summary>
	private async Task<(HashSet<string> Done, Dictionary<string, HashSet<long>> Written)> ResolveRestoredProgressAsync(
		InstallItem install,
		IReadOnlyList<FileManifest> files,
		InstallManagerState state)
	{
		var ledger = state.CompletedFiles?
			.GroupBy(record => record.Filename, StringComparer.Ordinal)
			.ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
		var partial = (state.WrittenParts ?? [])
			.GroupBy(record => record.Filename, StringComparer.Ordinal)
			.ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

		var done = new HashSet<string>(StringComparer.Ordinal);
		var written = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
		var toHash = new List<(FileManifest File, string Path)>();
		var cancellationToken = RestoreCheckToken;
		foreach (var file in files)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (file.ChunkParts.Count == 0)
				continue;
			var path = InstallPathPolicy.ResolveFile(install.Location, file.Filename);
			var info = new FileInfo(path);

			if (ledger is not null && ledger.TryGetValue(file.Filename, out var record) && record.Matches(file, info))
			{
				done.Add(file.Filename);
				_completedFiles[file.Filename] = record;
				continue;
			}

			// Anything below keeps at least part of the file, which only works when the file
			// still has the size its parts were written into.
			if (!info.Exists || info.Length != file.FileSize)
				continue;

			if (partial.TryGetValue(file.Filename, out var parts) &&
				string.Equals(parts.Sha, Convert.ToHexString(file.ShaHash), StringComparison.OrdinalIgnoreCase))
			{
				written[file.Filename] = [.. parts.FileOffsets];
				continue;
			}

			if (ledger is null)
				toHash.Add((file, path));
		}

		var hashed = new ConcurrentBag<FileManifest>();
		await Parallel.ForEachAsync(
			toHash,
			new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
			(candidate, token) =>
			{
				if (FileContentsMatch(candidate.Path, candidate.File, token))
					hashed.Add(candidate.File);
				return ValueTask.CompletedTask;
			});
		foreach (var file in hashed)
		{
			done.Add(file.Filename);
			_completedFiles[file.Filename] = CompletedFileRecord.From(
				file,
				new FileInfo(InstallPathPolicy.ResolveFile(install.Location, file.Filename)));
		}

		_logger.Information(
			"LoadPendingInstalls: {AppName}: {Done}/{Total} files complete ({Trusted} from the ledger, {Hashed} of {Candidates} verified by hash), {Partial} partially written",
			install.AppName, done.Count, files.Count, done.Count - hashed.Count, hashed.Count, toHash.Count, written.Count);
		return (done, written);
	}

	private void BuildPlan(
		Manifest data,
		IReadOnlyList<FileManifest> files,
		IReadOnlySet<string> done,
		IReadOnlyDictionary<string, HashSet<long>> written)
	{
		var install = CurrentInstall;
		var queuedChunks = new HashSet<BigInteger>();
		double doneBytes = 0;
		double writeBytes = 0;
		double downloadBytes = 0;

		foreach (var file in files)
		{
			if (file.ChunkParts.Count == 0)
			{
				// Manifest entries without chunks (e.g. DO_NOT_DELETE.txt) are created directly.
				CreateEmptyFile(file);
				continue;
			}

			if (done.Contains(file.Filename))
			{
				doneBytes += file.FileSize;
				continue;
			}

			var path = InstallPathPolicy.ResolveFile(install.Location, file.Filename);
			written.TryGetValue(file.Filename, out var writtenOffsets);
			var progress = new PartialFileProgress(Convert.ToHexString(file.ShaHash));
			var remaining = 0;
			foreach (var part in file.ChunkParts)
			{
				if (writtenOffsets?.Contains(part.FileOffset) == true)
				{
					doneBytes += part.Size;
					progress.FileOffsets.TryAdd(part.FileOffset, 0);
					// Marks the part as handled so the chunk's other parts do not rewrite it.
					_ioQueueTaskSet.TryAdd($"{file.Filename}.{part.GuidNum}.{part.FileOffset}", 0);
					continue;
				}

				remaining++;
				writeBytes += part.Size;
				var targets = _chunkToFileManifestsDictionary.GetOrAdd(part.GuidNum, _ => []);
				if (!targets.Contains(file))
					targets.Add(file);
				_chunkPartReferences.AddOrUpdate(part.GuidNum, 1, (_, count) => count + 1);

				if (!queuedChunks.Add(part.GuidNum))
					continue;
				var chunkInfo = data.CDL.GetChunkByGuidNum(part.GuidNum);
				_downloadQueue.Add(new DownloadTask
				{
					Url = chunkInfo.Path,
					TempPath = Path.Combine(install.Location, ".Crimson", chunkInfo.GuidNum + ".chunk"),
					GuidNum = chunkInfo.GuidNum,
					ChunkInfo = chunkInfo
				});
				downloadBytes += chunkInfo.FileSize;
			}

			if (remaining == 0)
			{
				// Every part was written but the completion was never recorded (the snapshot was
				// taken in between), so the file is complete.
				_completedFiles[file.Filename] = CompletedFileRecord.From(file, new FileInfo(path));
				continue;
			}

			_plannedFiles[path] = file;
			_partsRemaining[path] = remaining;
			if (!progress.FileOffsets.IsEmpty)
				_writtenParts[file.Filename] = progress;
		}

		// Totals accumulate: content changes plan their deletions before the new files.
		install.TotalDownloadSizeBytes += downloadBytes;
		install.TotalDownloadSizeMiB = install.TotalDownloadSizeBytes / 1024.0 / 1024.0;
		install.TotalWriteSizeBytes += doneBytes + writeBytes;
		install.TotalWriteSizeMb = install.TotalWriteSizeBytes / 1024.0 / 1024.0;
		install.WrittenSizeMiB += doneBytes / 1024.0 / 1024.0;

		_logger.Information(
			"BuildPlan: {Chunks} chunks ({DownloadMiB:F1} MiB) to download for {Files} files; {DoneMiB:F0} MiB already on disk",
			queuedChunks.Count, downloadBytes / 1024.0 / 1024.0, files.Count, doneBytes / 1024.0 / 1024.0);
	}

	private void CreateEmptyFile(FileManifest fileManifest)
	{
		var filePath = InstallPathPolicy.ResolveFile(CurrentInstall.Location, fileManifest.Filename);
		EnsureDirectoryExists(filePath);
		File.Create(filePath).Dispose();
		// No copy task will ever report this file, so account for its bytes here;
		// otherwise the progress total can never be reached.
		UpdateInstallWriteProgress(fileManifest.FileSize);
	}

	/// <summary>
	/// Prepare update tasks by comparing old and new manifests.
	/// Downloads only chunks for changed/added files and deletes removed files.
	/// Falls back to full reinstall if old manifest is unavailable.
	/// </summary>
	private async Task PrepareUpdateTasks(Game gameData, Manifest newManifest)
	{
		// Try to load the old manifest for the currently installed version
		_storage.LocalAppStateDictionary.TryGetValue(CurrentInstall.AppName, out var localAppState);

		if (localAppState == null || string.IsNullOrEmpty(localAppState.Version))
		{
			_logger.Warning("PrepareUpdateTasks: No installed version info, falling back to full install");
			CurrentInstall.Action = ActionType.Install;
			await _downloadManager.InitializeMirrors(gameData.BaseUrls);
			await PlanFilesAsync(newManifest, newManifest.FileManifestList.Elements);
			return;
		}

		var oldManifestBytes = await _storage.GetCachedManifestBytes(
			CurrentInstall.AppName, localAppState.Version);

		if (oldManifestBytes == null || oldManifestBytes.Length < 1)
		{
			_logger.Warning("PrepareUpdateTasks: Old manifest not cached, falling back to full install");
			CurrentInstall.Action = ActionType.Install;
			await _downloadManager.InitializeMirrors(gameData.BaseUrls);
			await PlanFilesAsync(newManifest, newManifest.FileManifestList.Elements);
			return;
		}

		var oldManifest = SelectManifest(Manifest.ReadAll(oldManifestBytes), localAppState.SelectedInstallTags);
		_logger.Information("PrepareUpdateTasks: Comparing manifests for {AppName}", CurrentInstall.AppName);

		// Build lookup of old file hashes by filename
		var oldFileHashes = new Dictionary<string, byte[]>();
		foreach (var oldFile in oldManifest.FileManifestList.Elements)
		{
			oldFileHashes[oldFile.Filename] = oldFile.ShaHash;
		}

		// Categorize files: unchanged, changed, added, removed
		var changedFiles = new List<FileManifest>();
		var addedFiles = new List<FileManifest>();
		var unchangedCount = 0;

		foreach (var newFile in newManifest.FileManifestList.Elements)
		{
			if (oldFileHashes.TryGetValue(newFile.Filename, out var oldHash))
			{
				oldFileHashes.Remove(newFile.Filename);
				if (oldHash.AsSpan().SequenceEqual(newFile.ShaHash))
					unchangedCount++;
				else
					changedFiles.Add(newFile);
			}
			else
			{
				addedFiles.Add(newFile);
			}
		}

		// Remaining old files are removed in the new version
		var removedFiles = oldFileHashes.Keys.ToList();

		_logger.Information(
			"PrepareUpdateTasks: {Unchanged} unchanged, {Changed} changed, {Added} added, {Removed} removed files",
			unchangedCount, changedFiles.Count, addedFiles.Count, removedFiles.Count);

		// Queue delete tasks for removed files
		foreach (var removedFile in removedFiles)
		{
			var filePath = InstallPathPolicy.ResolveFile(CurrentInstall.Location, removedFile);
			if (File.Exists(filePath))
			{
				_ioQueue.Add(new IoTask()
				{
					DestinationFilePath = filePath,
					TaskType = IoTaskType.Delete,
					Size = 0,
				});
			}
		}

		// Build filtered file manifest list containing only changed + added files
		var filesToDownload = new List<FileManifest>();
		filesToDownload.AddRange(changedFiles);
		filesToDownload.AddRange(addedFiles);

		if (filesToDownload.Count == 0)
		{
			_logger.Information("PrepareUpdateTasks: No files need updating");
			return;
		}

		await _downloadManager.InitializeMirrors(gameData.BaseUrls);
		await PlanFilesAsync(newManifest, filesToDownload);
	}

	/// <summary>
	/// Verify installed files and re-download only broken/missing ones
	/// </summary>
	private async Task PrepareRepairTasks(Game gameData, Manifest manifest)
	{
		_logger.Information("PrepareRepairTasks: Verifying files for {AppName}", CurrentInstall.AppName);

		var invalidFiles = await VerifyFiles(CurrentInstall.Location, manifest.FileManifestList.Elements);

		if (invalidFiles.Count == 0)
		{
			_logger.Information("PrepareRepairTasks: All files valid, nothing to repair");
			return;
		}

		_logger.Information("PrepareRepairTasks: {Count} files need repair", invalidFiles.Count);

		await _downloadManager.InitializeMirrors(gameData.BaseUrls);
		await PlanFilesAsync(manifest, invalidFiles);
	}

	private void UpdateDownloadProgress(long downloadedSize)
	{
		if (!IsInstallationInProgress())
			return;
		lock (_installItemLock)
		{
			CurrentInstall.DownloadedSizeMiB += downloadedSize / 1024.0 / 1024.0;
			CurrentInstall.DownloadSpeedRawMiB = _installStopWatch.IsRunning && _installStopWatch.Elapsed.TotalSeconds > 0
				? Math.Round(Math.Max(0, CurrentInstall.DownloadedSizeMiB - _sessionStartDownloadedMiB) / _installStopWatch.Elapsed.TotalSeconds, 2)
				: 0;

			UpdateProgressIfNeeded();
		}
	}

	private void UpdateInstallWriteProgress(long ioTaskSize)
	{
		if (!IsInstallationInProgress())
			return;
		lock (_installItemLock)
		{
			// WrittenSizeMiB is cumulative across restarts: restored progress is already in it,
			// so each completed io task only adds its own bytes here.
			CurrentInstall.WrittenSizeMiB += ioTaskSize / 1024.0 / 1024.0;

			// bad very bad, should not happen
			if (CurrentInstall.TotalWriteSizeMb < CurrentInstall.WrittenSizeMiB)
			{
				return;
			}

			CurrentInstall.WriteSpeedMiB = _installStopWatch.IsRunning && _installStopWatch.Elapsed.TotalSeconds > 0
				? Math.Round(Math.Max(0, CurrentInstall.WrittenSizeMiB - _sessionStartWrittenMiB) / _installStopWatch.Elapsed.TotalSeconds, 2)
				: 0;

			// An empty plan has nothing left to write, so it is complete.
			CurrentInstall.ProgressPercentage = CurrentInstall.TotalWriteSizeMb <= 0
				? 100
				: GetWrittenPercentage(CurrentInstall);
			UpdateProgressIfNeeded();
		}
	}

	private static void UpdateProgressPercentage(InstallItem install) =>
		install.ProgressPercentage = install.TotalWriteSizeMb <= 0 ? 0 : GetWrittenPercentage(install);

	// Round rather than truncate: truncation left the bar at 99% once the final bytes landed.
	// Clamp so float drift can never overshoot either.
	private static int GetWrittenPercentage(InstallItem install) =>
		Math.Clamp((int)Math.Round(install.WrittenSizeMiB / install.TotalWriteSizeMb * 100), 0, 100);
	private void UpdateProgressIfNeeded()
	{
		if (!IsInstallationInProgress())
			return;
		// Limit firing progress update events
		var now = Environment.TickCount64;
		if (now - _lastUpdateTick >= _progressUpdateIntervalInMS)
		{
			_lastUpdateTick = now;
			InstallProgressUpdate?.Invoke(CurrentInstall);
		}
		// Periodically checkpoint the active install so a crash, reboot, or ungraceful exit
		// cannot orphan the download. The snapshot is only a hint: restore re-verifies disk.
		if (now - _lastCheckpointTick >= _checkpointIntervalInMS)
		{
			_lastCheckpointTick = now;
			CheckpointActiveInstall();
		}
	}

	/// <summary>
	/// Checkpoints the active install. Failures are logged but never propagate: losing a
	/// checkpoint must not take down the download it was protecting.
	/// </summary>
	private void CheckpointActiveInstall()
	{
		try
		{
			SaveActiveInstallState();
		}
		catch (Exception ex)
		{
			_logger.Warning(ex, "Failed to checkpoint the active install state");
		}
	}

	private async Task HandleInstallationStoppage(string errorMessage)
	{
		_ioQueue = new();
		_downloadQueue = new();

		if (CurrentInstall == null)
		{
			_logger.Error("HandleInstallationStoppage called with no active install: {ErrorMessage}", errorMessage);
			ProcessNext();
			return;
		}

		if (!_userCancelled)
		{
			CancelRemainingContentActions(CurrentInstall);
			// propage cancelling status if not done already
			await _cancellationTokenSource.CancelAsync();
			CurrentInstall.Status = ActionStatus.Cancelling;
			InstallationStatusChanged?.Invoke(CurrentInstall);

			if (_downloadTasks != null) await Task.WhenAll(_downloadTasks);
			if (_installTasks != null) await Task.WhenAll(_installTasks);

			CurrentInstall.StatusMessage = string.IsNullOrWhiteSpace(errorMessage)
				? "Installation failed"
				: errorMessage;
			CurrentInstall.Status = ActionStatus.Failed;
			_installHistory.Add(CurrentInstall);
			_logger.Error("Installation failed: {ErrorMessage}", errorMessage);
		}
		else
		{
			CurrentInstall.Status = ActionStatus.Cancelling;
			InstallationStatusChanged?.Invoke(CurrentInstall);

			if (_downloadTasks != null) await Task.WhenAll(_downloadTasks);
			if (_installTasks != null) await Task.WhenAll(_installTasks);

			// Clean up only the temp chunk directory, not the entire game folder
			var tempChunkDir = Path.Combine(CurrentInstall.Location, ".Crimson");
			try
			{
				if (Directory.Exists(tempChunkDir))
					Directory.Delete(tempChunkDir, true);
			}
			catch (Exception ex)
			{
				_logger.Warning(ex, "Failed to clean up temp directory {Dir}", tempChunkDir);
			}

			await DeleteCancelledInstallFilesAsync(CurrentInstall);

			// A cancelled action installed nothing, so it is not listed under previous installs.
			CurrentInstall.Status = ActionStatus.Cancelled;
			_logger.Information("Installation cancelled");
		}
		var stoppedInstall = CurrentInstall;
		CurrentInstall = null;
		if (_userCancelled)
		{
			InstallManagerStateRemoved(stoppedInstall);
		}
		else
		{
			// A failed download keeps its persisted snapshot so the next start can resume it;
			// only the user cancelling an install discards the downloaded data.
			lock (_installItemLock)
				_suspendedStates.Remove(stoppedInstall.AppName);
			_logger.Information("Kept the saved install state of {AppName} after failure", stoppedInstall.AppName);
		}

		var json = JsonSerializer.Serialize(new InstallManagerState(), _jsonSerializerOptions);
		_storage.SaveInstallState(json);
		InstallationStatusChanged?.Invoke(stoppedInstall);

		ProcessNext();
	}

	/// <summary>
	///  Retrieve the Total Size to download and as well as space for install
	/// </summary>
	/// <param name="appName"></param>
	/// <returns></returns>
	public async Task<(double totalDownloadSizeMb, double totalWriteSizeMb)> GetGameDownloadInstallSizes(
		string appName,
		CancellationToken cancellationToken = default)
	{
		_logger.Information("GetGameDownloadInstallSizes: Getting game manifest for {AppName}", appName);

		var manifestData = await GetManifestDataWithCaching(appName, cancellationToken);

		_logger.Information("GetGameDownloadInstallSizes: Parsing game manifest for {AppName}", appName);
		var manifest = Manifest.ReadAll(manifestData);
		var (totalDownloadSizeBytes, totalWriteSizeBytes) = CalculateManifestSizes(manifest);
		_logger.Information("GetGameDownloadInstallSizes: Download size {DownloadBytes} bytes and write size {WriteBytes} bytes",
			totalDownloadSizeBytes, totalWriteSizeBytes);
		return (totalDownloadSizeBytes, totalWriteSizeBytes);
	}

	internal static (double DownloadBytes, double WriteBytes) CalculateManifestSizes(Manifest manifest)
	{
		ArgumentNullException.ThrowIfNull(manifest);
		var addedChunkGuids = new HashSet<BigInteger>();
		double downloadBytes = 0;
		double writeBytes = 0;

		foreach (var fileManifest in manifest.FileManifestList.Elements)
		{
			foreach (var chunkPart in fileManifest.ChunkParts)
			{
				if (addedChunkGuids.Add(chunkPart.GuidNum))
					downloadBytes += manifest.CDL.GetChunkByGuidNum(chunkPart.GuidNum).FileSize;
			}

			writeBytes += fileManifest.FileSize;
		}

		return (downloadBytes, writeBytes);
	}

	private async Task<byte[]> GetManifestDataWithCaching(
		string appName,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		byte[] manifestData = null;
		var gameData = _libraryManager.GetGameInfo(appName);

		// Check for cached manifest
		_storage.LocalAppStateDictionary.TryGetValue(appName, out var localAppState);
		var useCache = localAppState != null && gameData.BaseUrls != null &&
						gameData.AssetInfos.Windows.BuildVersion == localAppState.CachedManifestVersion;

		if (useCache)
		{
			manifestData = await _storage.GetCachedManifestBytes(appName, gameData.AssetInfos.Windows.BuildVersion);
		}

		// If we don't have manifest data yet (cache miss or not using cache)
		if (manifestData == null || manifestData.Length < 1)
		{
			// Get URLs from repository
			var urlData = await _repository.GetManifestUrls(
				gameData.AssetInfos.Windows.Namespace,
				gameData.AssetInfos.Windows.CatalogItemId,
				gameData.AppName,
				cancellationToken: cancellationToken);

			if (urlData == null)
			{
				_logger.Error("GetGameManifest: Failed to get manifest URLs for {AppName}", appName);
				throw new Exception("Cannot fetch manifest data");
			}

			gameData.BaseUrls = urlData.BaseUrls;
			_storage.SaveMetaData(gameData);

			// Download the manifest and cache it
			manifestData = await _repository.GetGameManifest(urlData, cancellationToken);
			cancellationToken.ThrowIfCancellationRequested();
			await _storage.CacheManifestBytes(appName, gameData.AssetInfos.Windows.BuildVersion, manifestData);
			cancellationToken.ThrowIfCancellationRequested();

			if (localAppState == null)
			{
				localAppState = new LocalAppState()
				{
					AppName = appName,
					CachedManifestVersion = gameData.AssetInfos.Windows.BuildVersion,
					InstallStatus = InstallState.NotInstalled
				};
			}
			else
			{
				localAppState.CachedManifestVersion = gameData.AssetInfos.Windows.BuildVersion;
			}
			_storage.AddToLocalAppState(appName, localAppState);
		}

		return manifestData;
	}

	public InstallItem GameGameInQueue(string gameName)
	{
		InstallItem item;
		if (CurrentInstall != null && CurrentInstall.AppName == gameName)
			item = CurrentInstall;
		else
			item = _installQueue.FirstOrDefault(r => r.AppName == gameName);
		return item;
	}

	public void CancelInstall(string appName)
	{
		if (string.IsNullOrEmpty(appName))
		{
			_logger.Warning("RemoveFromQueue: Invalid app name provided");
		}

		if (CurrentInstall?.AppName == appName)
		{
			if (_pendingRestoreInProgress)
			{
				_ = Task.Run(async () =>
				{
					await _restoreGate.WaitAsync();
					try
					{
						if (CurrentInstall?.AppName == appName)
							await StopProcessing();
					}
					finally
					{
						_restoreGate.Release();
					}
				});
			}
			else
			{
				_ = StopProcessing();
			}
		}

		var removedItem = _installQueue.RemoveAll(item => item.AppName == appName);
		if (removedItem > 0)
		{
			_logger.Information("RemoveFromQueue: Removed {AppName} from the install queue", appName);
		}

		if (IsSuspended(appName))
			CancelSuspendedInstall(appName);
	}

	public List<string> GetQueueItemNames()
	{
		return _installQueue.Select(item => item.AppName).ToList();
	}

	public List<string> GetHistoryItemsNames()
	{
		// Deduplicate: keep only the latest entry per AppName
		var seen = new HashSet<string>();
		var result = new List<string>();
		for (int i = _installHistory.Count - 1; i >= 0; i--)
		{
			if (seen.Add(_installHistory[i].AppName))
				result.Add(_installHistory[i].AppName);
		}
		result.Reverse();
		return result;
	}

	public Task StopProcessing()
	{
		InstallItem? install;
		lock (_installItemLock)
		{
			install = CurrentInstall;
			if (install is null)
			{
				_logger.Warning("StopProcessing: no active install to cancel");
				return Task.CompletedTask;
			}
			if (_isFinalizing)
			{
				_logger.Information("StopProcessing: finalization has started for {AppName}", install.AppName);
				return Task.CompletedTask;
			}

			install.Status = ActionStatus.Cancelling;
			_userCancelled = true;
		}

		_pauseEvent.Set();
		RequestStoppage("Cancel install");
		InstallationStatusChanged?.Invoke(install);
		return Task.CompletedTask;
	}

	private void RequestStoppage(string errorMessage)
	{
		Interlocked.CompareExchange(ref _stoppageReason, errorMessage, null);
		if (!_cancellationTokenSource.IsCancellationRequested)
			_cancellationTokenSource.Cancel();
	}

	private bool IsInstallationInProgress()
	{
		return CurrentInstall != null &&
			CurrentInstall.Status != ActionStatus.Cancelling &&
			CurrentInstall.Status != ActionStatus.Failed &&
			CurrentInstall.Status != ActionStatus.Cancelled &&
			CurrentInstall.Status != ActionStatus.Paused;
	}

	public void PauseInstall()
	{

		if (IsInstallationInProgress())
		{
			_logger.Debug("Pausing installation of {game}", CurrentInstall.AppName);
			_pauseEvent.Reset();

			Thread.Sleep(2000);

			// Snapshot under the progress lock so in-flight writes cannot mutate the item or the
			// completed-chunk list while they are being serialized.
			lock (_installItemLock)
			{
				_installStopWatch.Stop();
				CurrentInstall.Status = ActionStatus.Paused;
				PersistPausedState(CurrentInstall);
			}

			InstallationStatusChanged?.Invoke(CurrentInstall);
			_logger.Information("Saved installation state");
			_logger.Debug("Successfully paused installation of {game}", CurrentInstall.AppName);
		}
		else
			_logger.Warning("Installation of {appName} is not in progress {state}", CurrentInstall?.AppName, CurrentInstall?.Status);
	}

	/// <summary>
	/// Saves the paused install together with the live queues, both to disk and as the in-memory
	/// snapshot used when it is resumed. Callers hold <see cref="_installItemLock"/> or own the
	/// pipeline exclusively.
	/// </summary>
	private void PersistPausedState(InstallItem install)
	{
		var state = CaptureState(install);
		_suspendedStates[install.AppName] = state;
		_storage.SaveInstallState(install.AppName, JsonSerializer.Serialize(state, _jsonSerializerOptions));
	}

	/// <summary>
	/// Snapshot of the install and its progress ledger. Workers may still be adding to the ledger;
	/// a snapshot that misses their latest parts only means those parts are written again.
	/// </summary>
	private InstallManagerState CaptureState(InstallItem install) => new()
	{
		CurrentInstall = install,
		CompletedFiles = [.. _completedFiles.Values],
		WrittenParts = [.. _writtenParts.Select(entry => new PartialFileRecord
		{
			Filename = entry.Key,
			Sha = entry.Value.Sha,
			FileOffsets = [.. entry.Value.FileOffsets.Keys]
		})]
	};

	/// <summary>
	/// Writes a resumable snapshot of the install being processed. The snapshot is restored on the
	/// next start like a paused install; it may lag the live state, which only costs rewriting the
	/// parts written since. Used by the periodic checkpoint and the application shutdown path.
	/// </summary>
	public void SaveActiveInstallState()
	{
		if (CurrentInstall is null)
			return;

		lock (_installItemLock)
		{
			if (_isFinalizing || !IsInstallationInProgress())
				return;

			var json = JsonSerializer.Serialize(CaptureState(CurrentInstall), _jsonSerializerOptions);
			_storage.SaveInstallState(CurrentInstall.AppName, json);
			_logger.Debug("Checkpointed the install state of {AppName}", CurrentInstall.AppName);
		}
	}

	public void ResumeInstall()
	{
		if (_pendingRestoreInProgress)
		{
			_ = Task.Run(async () =>
			{
				await _restoreGate.WaitAsync();
				try
				{
					ResumeInstallCore();
				}
				finally
				{
					_restoreGate.Release();
				}
			});
			return;
		}

		ResumeInstallCore();
	}

	private void ResumeInstallCore()
	{
		if (CurrentInstall?.Status != ActionStatus.Paused)
		{
			_logger.Warning("No paused installation is available to resume");
			return;
		}

		// Workers are only absent after a restart (LoadPendingInstalls); in that case
		// the pipeline has to be rebuilt. Otherwise the original workers are still
		// alive and parked on _pauseEvent, so releasing them is enough. Calling
		// ProcessNext here would spawn a second set of workers over the same queues
		// and re-invoke _downloadQueue.CompleteAdding().
		if (_downloadTasks is null || _installTasks is null)
		{
			// A restored download only has live workers when its queues were rebuilt from
			// the saved state; otherwise preparation has to run again from the queue.
			if (!CurrentInstall.QueuesRestored)
			{
				_installQueue.Insert(0, CurrentInstall);
				CurrentInstall = null;
				ProcessNext();
				return;
			}

			ProcessNext(true);
			return;
		}

		_installStopWatch.Start();
		CurrentInstall.Status = ActionStatus.Processing;
		_pauseEvent.Set();
		InstallationStatusChanged?.Invoke(CurrentInstall);
	}

	/// <summary>
	/// Installs that were pushed off the pipeline by preemption or restored from disk.
	/// </summary>
	public IReadOnlyList<InstallItem> SuspendedInstalls
	{
		get
		{
			lock (_installItemLock)
				return _suspendedInstalls.ToList();
		}
	}

	public bool IsSuspended(string appName)
	{
		lock (_installItemLock)
			return _suspendedInstalls.Exists(item => item.AppName == appName);
	}

	/// <summary>
	/// Resumes a suspended install immediately: whatever is using the download pipeline right
	/// now is paused (kept resumable) and the requested install takes its place.
	/// </summary>
	public Task ResumeSuspendedInstallAsync(string appName) => PreemptCurrentInstallAsync(resumeAppName: appName);

	public void CancelSuspendedInstall(string appName)
	{
		if (string.IsNullOrEmpty(appName))
			return;

		InstallItem item;
		lock (_installItemLock)
		{
			var index = _suspendedInstalls.FindIndex(i => i.AppName == appName);
			if (index < 0)
			{
				_logger.Warning("CancelSuspendedInstall: {AppName} is not in the paused list", appName);
				return;
			}
			item = _suspendedInstalls[index];
			_suspendedInstalls.RemoveAt(index);
			_suspendedStates.Remove(appName);
		}

		_storage.DeleteInstallState(appName);
		item.Status = ActionStatus.Cancelled;
		_logger.Information("CancelSuspendedInstall: Removed {AppName} from the paused list", appName);
		InstallationStatusChanged?.Invoke(item);
		_ = Task.Run(() => DeleteCancelledInstallFilesAsync(item));
	}

	private async Task PreemptCurrentInstallAsync(string resumeAppName)
	{
		await Task.Run(async () =>
		{
			// A file check holding the gate would otherwise keep this request waiting until the
			// check finishes; the interrupted install stays paused and checks again when resumed.
			Interlocked.Increment(ref _pendingPreemptions);
			CancelRestoreCheck();
			try
			{
				await _restoreGate.WaitAsync();
			}
			finally
			{
				Interlocked.Decrement(ref _pendingPreemptions);
			}
			await _pipelineChangeLock.WaitAsync();
			try
			{
				await SuspendCurrentInstallAsync();

				if (resumeAppName is not null)
				{
					await StartSuspendedInstallAsync(resumeAppName);
				}
				else if (CurrentInstall == null && _installQueue.Count > 0)
				{
					ProcessNext();
				}
			}
			catch (Exception ex)
			{
				_logger.Error(ex, "PreemptCurrentInstall: Failed to hand the pipeline over");
			}
			finally
			{
				_pipelineChangeLock.Release();
				_restoreGate.Release();
			}
		});
	}

	/// <summary>
	/// Pauses the install currently holding the pipeline and moves it into the suspended list so
	/// another install can start. Returns with the pipeline released (CurrentInstall null) or
	/// unchanged when the current install cannot be paused (already cancelling/finalizing).
	/// </summary>
	private async Task SuspendCurrentInstallAsync()
	{
		InstallItem install;
		lock (_installItemLock)
		{
			install = CurrentInstall;
			if (install == null)
				return;
			if (install.Status == ActionStatus.Processing && _isFinalizing)
			{
				_logger.Information("SuspendCurrentInstall: {AppName} is finalizing; leaving it to finish", install.AppName);
				return;
			}
			if (install.Status is not (ActionStatus.Processing or ActionStatus.Paused))
			{
				_logger.Warning("SuspendCurrentInstall: {AppName} is {Status}; leaving it alone", install.AppName, install.Status);
				return;
			}
		}

		if (install.Status == ActionStatus.Processing)
			PauseInstall();

		if (!ReferenceEquals(CurrentInstall, install) || CurrentInstall?.Status != ActionStatus.Paused)
			return;

		var workers = (_downloadTasks ?? Enumerable.Empty<Task>())
			.Concat(_installTasks ?? Enumerable.Empty<Task>())
			.ToArray();

		if (workers.Length == 0 || workers.All(task => task.IsCompleted))
		{
			// Restored installs (and installs without a live pipeline) move directly. A restored
			// install's queues reflect the disk check, so save them before they are reset; one whose
			// rebuild failed keeps its previous snapshot.
			if (install.QueuesRestored)
			{
				lock (_installItemLock)
					PersistPausedState(install);
			}
			RegisterSuspended(install);
			CurrentInstall = null;
			_logger.Information("SuspendCurrentInstall: {AppName} moved to the paused list", install.AppName);
			InstallationStatusChanged?.Invoke(null);
			return;
		}

		var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_suspensionCompletion = completion;
		_suspensionRequested = true;
		_pauseEvent.Set();
		await _cancellationTokenSource.CancelAsync();

		// The winding-down ProcessNext continuation performs the hand-off in HandleSuspension.
		await completion.Task;
	}

	private void RegisterSuspended(InstallItem install)
	{
		lock (_installItemLock)
		{
			if (!_suspendedInstalls.Exists(item => item.AppName == install.AppName))
				_suspendedInstalls.Add(install);
		}
	}

	private async Task StartSuspendedInstallAsync(string appName)
	{
		InstallItem target;
		InstallManagerState snapshot;
		lock (_installItemLock)
		{
			var index = _suspendedInstalls.FindIndex(item => item.AppName == appName);
			if (index < 0)
			{
				_logger.Warning("StartSuspendedInstall: {AppName} is not in the paused list", appName);
				return;
			}
			target = _suspendedInstalls[index];
			_suspendedInstalls.RemoveAt(index);
			_suspendedStates.TryGetValue(appName, out snapshot);
		}

		if (CurrentInstall != null)
		{
			_logger.Warning("StartSuspendedInstall: Pipeline still busy; returning {AppName} to the paused list", appName);
			lock (_installItemLock)
				_suspendedInstalls.Add(target);
			return;
		}

		CurrentInstall = target;
		try
		{
			if (!await RestoreSavedInstallAsync(snapshot ?? new InstallManagerState { CurrentInstall = target }))
			{
				// Another resume request interrupted the check; it takes the pipeline next.
				RegisterSuspended(target);
				CurrentInstall = null;
				InstallationStatusChanged?.Invoke(null);
				return;
			}
		}
		catch (Exception ex)
		{
			_logger.Error(ex, "StartSuspendedInstall: Failed to rebuild work for {AppName}", appName);
			ResetQueues();
			lock (_installItemLock)
				_suspendedInstalls.Add(target);
			CurrentInstall = null;
			target.Status = ActionStatus.Paused;
			InstallationStatusChanged?.Invoke(target);
			if (_installQueue.Count > 0)
				ProcessNext();
			return;
		}

		if (!target.QueuesRestored)
		{
			// The rebuild failed once already; let the regular queue path rebuild from the manifest.
			_installQueue.Insert(0, target);
			CurrentInstall = null;
			ProcessNext();
			return;
		}

		_logger.Information("StartSuspendedInstall: Resuming {AppName}", appName);
		ProcessNext(true);
	}

	/// <summary>
	/// Hand-off for an install whose workers were cancelled to free the pipeline for another
	/// game: the install stays paused with its snapshot intact instead of being cancelled.
	/// </summary>
	private void HandleSuspension()
	{
		_ioQueue = new();
		_downloadQueue = new();

		var install = CurrentInstall;
		if (install != null)
		{
			install.Status = ActionStatus.Paused;
			// The pause snapshot was taken before the workers wound down; parts they finished
			// since then belong in the ledger too.
			try
			{
				lock (_installItemLock)
					PersistPausedState(install);
			}
			catch (Exception ex)
			{
				_logger.Warning(ex, "HandleSuspension: Failed to save the state of {AppName}", install.AppName);
			}
			RegisterSuspended(install);
			CurrentInstall = null;
			_logger.Information("HandleSuspension: {AppName} paused so the next install can start", install.AppName);
			InstallationStatusChanged?.Invoke(null);
		}
		else
		{
			_logger.Error("HandleSuspension called with no active install");
		}

		_suspensionRequested = false;
		Interlocked.Exchange(ref _suspensionCompletion, null)?.TrySetResult();
	}

	private async Task HandleStoppageOrSuspensionAsync(string errorMessage)
	{
		if (_suspensionRequested && !_userCancelled)
			HandleSuspension();
		else
			await HandleInstallationStoppage(errorMessage);
	}

	public async Task LoadPendingInstalls()
	{
		var savedStates = new List<InstallManagerState>();
		var seenApps = new HashSet<string>(StringComparer.Ordinal);

		// Legacy single-file state. It is migrated into the per-app store so every paused game
		// keeps its own snapshot from now on.
		string jsonData = null;
		try
		{
			jsonData = _storage.GetInstallState();
		}
		catch (Exception ex)
		{
			_logger.Warning(ex, "LoadPendingInstalls: No legacy install state could be read");
		}

		if (!string.IsNullOrEmpty(jsonData))
		{
			InstallManagerState legacyState = null;
			try
			{
				legacyState = JsonSerializer.Deserialize<InstallManagerState>(jsonData, _jsonSerializerOptions);
			}
			catch (Exception ex)
			{
				_logger.Error(ex, "LoadPendingInstalls: Legacy install state could not be parsed");
			}

			if (legacyState?.CurrentInstall is not null)
			{
				if (seenApps.Add(legacyState.CurrentInstall.AppName))
				{
					savedStates.Add(legacyState);
					_storage.SaveInstallState(
						legacyState.CurrentInstall.AppName,
						JsonSerializer.Serialize(legacyState, _jsonSerializerOptions));
				}

				var emptyState = JsonSerializer.Serialize(
					new InstallManagerState(),
					_jsonSerializerOptions);
				_storage.SaveInstallState(emptyState);
			}
		}

		foreach (var perAppJson in _storage.EnumerateInstallStates())
		{
			InstallManagerState state;
			try
			{
				state = JsonSerializer.Deserialize<InstallManagerState>(perAppJson, _jsonSerializerOptions);
			}
			catch (Exception ex)
			{
				_logger.Error(ex, "LoadPendingInstalls: A saved install state could not be parsed");
				continue;
			}

			if (state?.CurrentInstall is null || !seenApps.Add(state.CurrentInstall.AppName))
				continue;

			savedStates.Add(state);
		}

		if (savedStates.Count == 0)
		{
			_logger.Information("LoadPendingInstalls: No pending installs in the saved states");
			return;
		}

		// The first restored install keeps the current-download slot (paused, resumable); any
		// additional ones join the paused list and are rebuilt lazily when resumed. They are
		// listed before the first one's disk check so they stay visible meanwhile; resuming
		// one waits on the restore gate, which is held from here.
		await _restoreGate.WaitAsync();
		_pendingRestoreInProgress = true;
		try
		{
			foreach (var state in savedStates.Skip(1))
			{
				var item = state.CurrentInstall;
				item.Status = ActionStatus.Paused;
				lock (_installItemLock)
				{
					_suspendedInstalls.Add(item);
					_suspendedStates[item.AppName] = state;
				}
				_logger.Information("LoadPendingInstalls: {AppName} restored into the paused list", item.AppName);
				InstallationStatusChanged?.Invoke(null);
			}

			await RestoreSavedInstallAsync(savedStates[0]);
			// A successful restore already stored its verified snapshot; keep that one.
			lock (_installItemLock)
				_suspendedStates.TryAdd(savedStates[0].CurrentInstall.AppName, savedStates[0]);
		}
		finally
		{
			_pendingRestoreInProgress = false;
			_restoreGate.Release();
		}
	}

	private CancellationToken RestoreCheckToken => _restoreCheckCts?.Token ?? CancellationToken.None;

	private void CancelRestoreCheck()
	{
		try
		{
			_restoreCheckCts?.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
	}

	/// <summary>
	/// Rebuilds the work of a saved install and leaves it paused in the current-download slot.
	/// Returns false when a preemption cancelled the file check; the install keeps its snapshot.
	/// </summary>
	private async Task<bool> RestoreSavedInstallAsync(InstallManagerState state)
	{
		var savedInstall = state.CurrentInstall;
		var saved = (
			savedInstall.TotalDownloadSizeBytes,
			savedInstall.TotalDownloadSizeMiB,
			savedInstall.DownloadedSizeMiB,
			savedInstall.TotalWriteSizeBytes,
			savedInstall.TotalWriteSizeMb,
			savedInstall.WrittenSizeMiB);
		CurrentInstall = savedInstall;
		CurrentInstall.Status = ActionStatus.Paused;
		CurrentInstall.IsVerifying = true;
		InstallationStatusChanged?.Invoke(CurrentInstall);

		// Planning resumes from the snapshot's ledger: recorded files and parts are skipped, and
		// every size and progress figure is recomputed from the plan.
		var queuesRestored = true;
		var cancelled = false;
		var checkCts = new CancellationTokenSource();
		_restoreCheckCts = checkCts;
		if (Volatile.Read(ref _pendingPreemptions) > 0)
			checkCts.Cancel();
		_restoringFrom = state;
		try
		{
			await PrepareTasks(true);
		}
		catch (OperationCanceledException) when (checkCts.IsCancellationRequested)
		{
			queuesRestored = false;
			cancelled = true;
			(savedInstall.TotalDownloadSizeBytes, savedInstall.TotalDownloadSizeMiB, savedInstall.DownloadedSizeMiB,
				savedInstall.TotalWriteSizeBytes, savedInstall.TotalWriteSizeMb, savedInstall.WrittenSizeMiB) = saved;
			ResetQueues();
			_logger.Information("LoadPendingInstalls: File check of {AppName} interrupted by another install", savedInstall.AppName);
		}
		catch (Exception ex)
		{
			// Keep the saved download resumable rather than throwing it away. Resuming drops
			// the item back into the install queue, which plans it from the same snapshot.
			queuesRestored = false;
			(savedInstall.TotalDownloadSizeBytes, savedInstall.TotalDownloadSizeMiB, savedInstall.DownloadedSizeMiB,
				savedInstall.TotalWriteSizeBytes, savedInstall.TotalWriteSizeMb, savedInstall.WrittenSizeMiB) = saved;
			_logger.Error(ex, "LoadPendingInstalls: Failed to restore queued work for {AppName}", savedInstall.AppName);
		}
		finally
		{
			_restoringFrom = null;
			_restoreCheckCts = null;
			checkCts.Dispose();
		}
		CurrentInstall.QueuesRestored = queuesRestored;

		if (queuesRestored && CurrentInstall.TotalWriteSizeMb - CurrentInstall.WrittenSizeMiB <= 0)
			_logger.Information(
				"LoadPendingInstalls: {AppName} has nothing left to download; resuming will verify it",
				savedInstall.AppName);

		CurrentInstall.Status = ActionStatus.Paused;
		CurrentInstall.IsVerifying = false;
		CurrentInstall.DownloadSpeedRawMiB = 0;
		CurrentInstall.WriteSpeedMiB = 0;
		// Save the ledger as the plan resolved it (a legacy snapshot becomes a ledger here), so the
		// next start can trust it instead of hashing the disk again.
		if (queuesRestored)
		{
			try
			{
				lock (_installItemLock)
					PersistPausedState(CurrentInstall);
			}
			catch (Exception ex)
			{
				_logger.Warning(ex, "LoadPendingInstalls: Failed to save the restored state of {AppName}", savedInstall.AppName);
			}
		}
		InstallationStatusChanged?.Invoke(CurrentInstall);
		_pauseEvent.Set();
		return !cancelled;
	}

	private static bool FileContentsMatch(string path, FileManifest file, CancellationToken cancellationToken)
	{
		try
		{
			var info = new FileInfo(path);
			return info.Exists && info.Length == file.FileSize && FileHash.Sha1Matches(path, file.ShaHash, cancellationToken);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			return false;
		}
	}
}

internal class InstallItemComparer : IEqualityComparer<InstallItem>
{
	public bool Equals(InstallItem x, InstallItem y)
	{
		return y != null && x != null && x.AppName == y.AppName;
	}

	public int GetHashCode(InstallItem obj)
	{
		return obj.AppName.GetHashCode();
	}
}

public class DownloadTask
{
	public string Url { get; set; }

	public BigInteger GuidNum { get; set; }

	public string TempPath { get; set; }

	public ChunkInfo ChunkInfo { get; set; }
}


public class IoTask
{
	public string SourceFilePath { get; set; }
	public string DestinationFilePath { get; set; }
	public long Size { get; set; }
	public long Offset { get; set; }
	public long FileOffset { get; set; }
	public long DestinationFileSize { get; set; }
	public IoTaskType TaskType { get; set; }
	public BigInteger GuidNum { get; set; }
	public BigInteger SourceChunkGuidNum { get; set; }
}

public enum IoTaskType
{
	Copy,
	Create,
	Delete,
	Read
}

public class InstallManagerState
{
	public InstallItem CurrentInstall { get; set; }

	/// <summary>
	/// Files this install finished writing. A file whose size and write time still match its
	/// record is trusted on restore without hashing it again. Null in snapshots written before
	/// the ledger existed; those are verified against the disk instead.
	/// </summary>
	public List<CompletedFileRecord>? CompletedFiles { get; set; }

	/// <summary>Parts already written into files that are not finished yet.</summary>
	public List<PartialFileRecord>? WrittenParts { get; set; }

	// Legacy snapshot fields. They are still read so old snapshots deserialize, but restores no
	// longer use them: the copy queue is rebuilt from the plan, and chunk-level completion could
	// not tell which files a chunk had actually reached.
	public List<IoTask>? IoQueue { get; set; }
	public List<BigInteger>? CompletedChunks { get; set; }
}

public sealed class CompletedFileRecord
{
	public required string Filename { get; init; }
	public required string Sha { get; init; }
	public long Size { get; init; }
	public long LastWriteUtcTicks { get; init; }

	public static CompletedFileRecord From(FileManifest file, FileInfo info) => new()
	{
		Filename = file.Filename,
		Sha = Convert.ToHexString(file.ShaHash),
		Size = file.FileSize,
		LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks
	};

	/// <summary>
	/// True when the record describes this manifest file and the file on disk has not been
	/// touched since it was recorded.
	/// </summary>
	public bool Matches(FileManifest file, FileInfo info) =>
		Size == file.FileSize &&
		string.Equals(Sha, Convert.ToHexString(file.ShaHash), StringComparison.OrdinalIgnoreCase) &&
		info.Exists &&
		info.Length == Size &&
		info.LastWriteTimeUtc.Ticks == LastWriteUtcTicks;
}

public sealed class PartialFileRecord
{
	public required string Filename { get; init; }
	public required string Sha { get; init; }
	public List<long> FileOffsets { get; init; } = [];
}
