using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Crimson.Core;
using Crimson.Models;
using Serilog;

namespace Crimson.Tests;

public sealed class InstallProgressTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), $"crimson-progress-{Guid.NewGuid():N}");
	private readonly Serilog.Core.Logger _logger = new LoggerConfiguration().CreateLogger();
	private readonly InstallManager _installer;

	public InstallProgressTests()
	{
		Directory.CreateDirectory(_root);
		_installer = new InstallManager(_logger, null!, null!, null!, null!, null!, null!);
	}

	[Fact]
	public void InstallReachesFullProgressWhenManifestHasFilesWithChunksAndEmptyFiles()
	{
		var install = new InstallItem("test", ActionType.Install, _root) { Status = ActionStatus.Processing };
		SetCurrentInstall(install);
		var payload = new FileManifest { Filename = "payload.bin", FileSize = 10 };
		var part = new ChunkPart([1, 0, 0, 0], size: 10);
		payload.ChunkParts.Add(part);
		// Two empty manifest entries far larger than the payload: leaving them
		// uncounted is exactly what used to leave the bar short of 100%.
		var emptyFirst = new FileManifest { Filename = "DO_NOT_DELETE.txt", FileSize = 0 };
		var emptySecond = new FileManifest { Filename = "empty.txt", FileSize = 0 };
		var manifest = new Manifest
		{
			CDL = new CDL(),
			FileManifestList = new FileManifestList()
		};
		manifest.CDL.Elements.Add(new ChunkInfo { Guid = [1, 0, 0, 0], FileSize = 10 });
		manifest.FileManifestList.Elements.Add(payload);
		manifest.FileManifestList.Elements.Add(emptyFirst);
		manifest.FileManifestList.Elements.Add(emptySecond);

		Invoke(
			"BuildPlan",
			manifest,
			manifest.FileManifestList.Elements,
			new HashSet<string>(),
			new Dictionary<string, HashSet<long>>());
		Invoke("UpdateInstallWriteProgress", 10L);

		Assert.Equal(100, install.ProgressPercentage);
		Assert.True(File.Exists(Path.Combine(_root, "DO_NOT_DELETE.txt")));
		Assert.True(File.Exists(Path.Combine(_root, "empty.txt")));
	}

	private void Invoke(string methodName, params object[] arguments) =>
		typeof(InstallManager)
			.GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(_installer, arguments);

	private void SetCurrentInstall(InstallItem item) =>
		typeof(InstallManager)
			.GetProperty(nameof(InstallManager.CurrentInstall), BindingFlags.Instance | BindingFlags.Public)!
			.SetValue(_installer, item);

	public void Dispose()
	{
		_logger.Dispose();
		if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
	}
}
