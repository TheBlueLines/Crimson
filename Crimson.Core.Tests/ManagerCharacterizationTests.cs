using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Crimson.Core;
using Crimson.Models;
using Crimson.Repository;
using Crimson.Utils;
using Serilog;

namespace Crimson.Tests;

public sealed class ManagerCharacterizationTests
{
	private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

	[Fact]
	public void LibraryManager_ReturnsOwnedGameAndItsDlcs()
	{
		var baseGame = Game("base", metadataId: "catalog-base");
		var matchingDlc = Game("matching-dlc", mainGameId: "catalog-base");
		var otherDlc = Game("other-dlc", mainGameId: "other-catalog");
		var storage = StorageWith(baseGame, matchingDlc, otherDlc);
		var manager = LibraryManagerWith(storage);

		Assert.Same(baseGame, manager.GetGameInfo("base"));
		Assert.Null(manager.GetGameInfo("missing"));
		Assert.Equal([matchingDlc], manager.GetDlcsForGame("base"));
		Assert.Empty(manager.GetDlcsForGame("matching-dlc"));
		Assert.Empty(manager.GetDlcsForGame("missing"));
	}

	[Fact]
	public void LibraryManager_FiltersCachedMetadataByCurrentOwnership()
	{
		var owned = Game("owned");
		var stale = Game("stale");
		var metadata = new Dictionary<string, Game>
		{
			[owned.AppName] = owned,
			[stale.AppName] = stale
		};
		var assets = new[]
		{
			Asset(owned.AppName),
			Asset("metadata-not-fetched")
		};

		var games = LibraryManager.SelectOwnedGames(assets, metadata);

		Assert.Equal([owned], games);
	}

	[Fact]
	public void LibraryManager_BuildsAuthenticatedLaunchArguments()
	{
		var game = Game("owned");
		game.Metadata.CustomAttributes = new CustomAttributes
		{
			AdditionalCommandLine = new AdditionalCommandLine { Value = "-from-metadata" }
		};
		var state = game.LocalAppState!;
		state.LaunchParameters = "-from-manifest";
		var user = new UserData { AccountId = "account", DisplayName = "Player Name" };

		var arguments = LibraryManager.BuildLaunchArguments(
			state,
			game,
			"exchange-code",
			user,
			"C:\\Temp\\game.ovt");

		Assert.Contains("-from-manifest", arguments);
		Assert.Contains("-from-metadata", arguments);
		Assert.Contains("-AUTH_PASSWORD=exchange-code", arguments);
		Assert.Contains("-epicapp=owned", arguments);
		Assert.Contains("-epicovt=\"C:\\Temp\\game.ovt\"", arguments);
		Assert.Contains("-epicuserid=account", arguments);
		Assert.Contains("-epicsandboxid=synthetic", arguments);
	}

	[Fact]
	public void InstallManager_QueuesValidActionsAndPreservesOrder()
	{
		var installable = Game("installable");
		var installed = Game("installed", InstallState.Installed);
		var broken = Game("broken", InstallState.Broken);
		var manager = InstallManagerWith(installable, installed, broken);
		SetCurrentInstall(manager, new InstallItem("active", ActionType.Install, "C:\\active"));

		var install = new InstallItem("installable", ActionType.Install, "C:\\installable");
		var duplicate = new InstallItem("installable", ActionType.Install, "C:\\duplicate");
		var update = new InstallItem("installed", ActionType.Update, "C:\\installed");
		var forcedRepair = new InstallItem("broken", ActionType.Update, "C:\\broken");

		manager.AddToQueue(install);
		manager.AddToQueue(duplicate);
		manager.AddToQueue(new InstallItem("missing", ActionType.Install, "C:\\missing"));
		manager.AddToQueue(new InstallItem("installable", ActionType.Update, "C:\\installable"));
		manager.AddToQueue(update);
		manager.AddToQueue(forcedRepair);

		Assert.Equal(["installable", "installed", "broken"], manager.GetQueueItemNames());
		Assert.Same(install, manager.GameGameInQueue("installable"));
		Assert.Same(update, manager.GameGameInQueue("installed"));
		Assert.Equal(ActionType.Repair, forcedRepair.Action);

		manager.CancelInstall("installed");

		Assert.Equal(["installable", "broken"], manager.GetQueueItemNames());
		Assert.Null(manager.GameGameInQueue("installed"));
	}

	[Fact]
	public async Task InstallManager_StopProcessingLeavesTeardownToProcessor()
	{
		var manager = InstallManagerWith();
		var install = new InstallItem("active", ActionType.Install, "C:\\active");
		SetCurrentInstall(manager, install);

		await manager.StopProcessing();

		var cancellation = GetPrivateField<CancellationTokenSource>(manager, "_cancellationTokenSource");
		Assert.True(cancellation.IsCancellationRequested);
		Assert.Same(install, manager.CurrentInstall);
		Assert.Equal(ActionStatus.Cancelling, install.Status);
	}

	[Fact]
	public void InstallManager_QueuesIoTasksFromConcurrentDownloads()
	{
		const int taskCount = 2_000;
		var manager = InstallManagerWith();
		SetCurrentInstall(manager, new InstallItem("active", ActionType.Install, Path.GetTempPath()));
		var manifests = GetPrivateField<ConcurrentDictionary<BigInteger, List<FileManifest>>>(
			manager,
			"_chunkToFileManifestsDictionary");
		var createIoTasks = typeof(InstallManager).GetMethod(
			"CreateIoTasksForChunk",
			BindingFlags.Instance | BindingFlags.NonPublic)!;
		var downloadTasks = Enumerable.Range(1, taskCount).Select(index =>
		{
			var part = new ChunkPart([index, 0, 0, 0], size: 1);
			var manifest = new FileManifest { Filename = $"file-{index}.bin" };
			manifest.ChunkParts.Add(part);
			manifests[part.GuidNum] = [manifest];
			return new DownloadTask
			{
				Url = string.Empty,
				GuidNum = part.GuidNum,
				TempPath = $"chunk-{index}",
				ChunkInfo = null!
			};
		}).ToList();

		var failure = Record.Exception(() => Parallel.ForEach(
			downloadTasks,
			new ParallelOptions { MaxDegreeOfParallelism = 12 },
			task => createIoTasks.Invoke(manager, [task])));

		Assert.Null(failure);
		var ioQueue = GetPrivateField<BlockingCollection<IoTask>>(manager, "_ioQueue");
		Assert.Equal(taskCount, ioQueue.Count);
	}

	[Fact]
	public void InstallManager_HistoryReturnsLatestEntryPerGameInStableOrder()
	{
		var manager = InstallManagerWith();
		var history = GetPrivateField<List<InstallItem>>(manager, "_installHistory");
		history.Add(new InstallItem("alpha", ActionType.Install, "C:\\alpha"));
		history.Add(new InstallItem("beta", ActionType.Install, "C:\\beta"));
		history.Add(new InstallItem("alpha", ActionType.Repair, "C:\\alpha"));

		Assert.Equal(["beta", "alpha"], manager.GetHistoryItemsNames());
	}

	private LibraryManager LibraryManagerWith(Storage storage)
	{
		var auth = new AuthManager(
			_logger,
			storage,
			new TestCredentialProtector(),
			new HttpClient());
		return new LibraryManager(_logger, new UnusedStoreRepository(), storage, auth);
	}

	private InstallManager InstallManagerWith(params Game[] games)
	{
		var storage = StorageWith(games);
		var library = LibraryManagerWith(storage);
		var downloads = new DownloadManager(_logger, new HttpClient());
		return new InstallManager(
			_logger,
			library,
			new UnusedStoreRepository(),
			storage,
			downloads,
			new UnusedGameShortcutManager(),
			new AllowInstallPermissionChecker());
	}

	private Storage StorageWith(params Game[] games)
	{
		var storage = (Storage)RuntimeHelpers.GetUninitializedObject(typeof(Storage));
		SetPrivateField(storage, "_gameMetaDataDictionary", games.ToDictionary(game => game.AppName));
		SetPrivateField(storage, "_localAppStateDictionary", games
			.Where(game => game.LocalAppState != null)
			.ToDictionary(game => game.AppName, game => game.LocalAppState!));
		SetPrivateField(storage, "_logger", _logger);
		return storage;
	}

	private static Asset Asset(string appName) => new()
	{
		AppName = appName,
		BuildVersion = "1.0.0",
		CatalogItemId = appName,
		Namespace = "synthetic"
	};

	private static Game Game(
		string appName,
		InstallState installState = InstallState.NotInstalled,
		string? metadataId = null,
		string? mainGameId = null) => new()
	{
		AppName = appName,
		AppTitle = appName,
		AssetInfos = new AssetInfos
		{
			Windows = new Asset
			{
				AppName = appName,
				BuildVersion = "1.0.0",
				CatalogItemId = metadataId ?? appName,
				Namespace = "synthetic"
			}
		},
		Metadata = new Metadata
		{
			Id = metadataId ?? appName,
			MainGameItem = mainGameId == null ? null! : new MainGameItem { Id = mainGameId }
		},
		LocalAppState = new LocalAppState
		{
			AppName = appName,
			InstallStatus = installState
		}
	};

	private static void SetCurrentInstall(InstallManager manager, InstallItem item) =>
		typeof(InstallManager)
			.GetProperty(nameof(InstallManager.CurrentInstall), BindingFlags.Instance | BindingFlags.Public)!
			.SetValue(manager, item);

	private static T GetPrivateField<T>(object instance, string name) =>
		(T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

	private static void SetPrivateField(object instance, string name, object value) =>
		instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);

	private sealed class UnusedGameShortcutManager : IGameShortcutManager
	{
		public Task CreateAsync(Game game, GameShortcutLocation location, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public void Remove(Game game)
		{
		}
	}

	private sealed class AllowInstallPermissionChecker : IInstallPermissionChecker
	{
		public InstallPermissionCheckResult Check(string folderPath) => new(true);
	}

	private sealed class UnusedStoreRepository : IStoreRepository
	{
		public Task<Metadata> FetchGameMetaData(string nameSpace, string catalogItemId) =>
			throw new NotSupportedException();

		public Task<IEnumerable<Asset>> FetchGameAssets(string platform = "Windows", string label = "Live") =>
			throw new NotSupportedException();

		public Task<byte[]> GetGameManifest(
			GetManifestUrlData urlData,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task DownloadFileAsync(string url, string destinationPath) =>
			throw new NotSupportedException();

		public Task<string> GetGameToken() => throw new NotSupportedException();

		public Task<byte[]?> GetOwnershipToken(string nameSpace, string catalogItemId) =>
			throw new NotSupportedException();

		public Task<GetManifestUrlData> GetManifestUrls(
			string nameSpace,
			string catalogItem,
			string appName,
			string platform = "Windows",
			string label = "Live",
			CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}
}
