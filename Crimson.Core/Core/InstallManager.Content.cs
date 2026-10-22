using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Models;

namespace Crimson.Core;

public partial class InstallManager
{
	public bool CanManageContent(string appName)
	{
		var game = _libraryManager.GetGameInfo(appName);
		var dlcs = _libraryManager.GetDlcsForGame(appName);
		var related = dlcs.Select(dlc => dlc.AppName).Append(appName).ToHashSet();
		var current = CurrentInstall;
		var busy = (current is not null &&
			current.Status is not ActionStatus.Success and not ActionStatus.Failed and not ActionStatus.Cancelled &&
			(related.Contains(current.AppName) || current.ContentOwnerAppName == appName)) ||
			_installQueue.Any(item => related.Contains(item.AppName) || item.ContentOwnerAppName == appName);
		return game is not null && !game.IsDlc() &&
			InstalledContentSelection.CanModify(game.LocalAppState, busy || _libraryManager.IsAppRunning(appName)) &&
			dlcs.All(dlc => dlc.LocalAppState?.InstallStatus is null or InstallState.NotInstalled or InstallState.Installed or InstallState.NeedUpdate);
	}

	public bool TryQueueContentChange(string appName, IEnumerable<string> selectedTags, IEnumerable<string> selectedDlcs)
	{
		if (!CanManageContent(appName))
			return false;

		var game = _libraryManager.GetGameInfo(appName);
		var dlcs = _libraryManager.GetDlcsForGame(appName);
		var selected = selectedDlcs.Distinct(StringComparer.Ordinal).ToList();
		if (selected.Except(dlcs.Select(dlc => dlc.AppName)).Any())
			throw new ArgumentException("The selection contains DLC that does not belong to this game.");

		var installed = dlcs.Where(dlc => dlc.LocalAppState?.InstallStatus is InstallState.Installed or InstallState.NeedUpdate).Select(dlc => dlc.AppName);
		var (added, removed) = InstalledContentSelection.Diff(installed, selected);
		var location = game.LocalAppState!.InstallPath;
		var items = new List<InstallItem>
		{
			new(appName, ActionType.ModifyContent, location)
			{
				SelectedInstallTags = selectedTags.Distinct(StringComparer.Ordinal).ToList(),
				ContentOwnerAppName = appName
			}
		};
		items.AddRange(removed.Select(name => new InstallItem(name, ActionType.Uninstall, dlcs.Single(dlc => dlc.AppName == name).LocalAppState!.InstallPath) { ContentOwnerAppName = appName }));
		items.AddRange(added.Select(name => new InstallItem(name, ActionType.Install, location) { ContentOwnerAppName = appName }));
		_installQueue.AddRange(items);
		foreach (var item in items)
			InstallationStatusChanged?.Invoke(item);
		if (CurrentInstall is null)
			ProcessNext();
		return true;
	}

	public async Task<Manifest> GetContentManifestAsync(string appName, bool installed, CancellationToken cancellationToken = default)
	{
		var bytes = installed
			? await GetInstalledManifestBytes(appName, cancellationToken)
			: await GetManifestDataWithCaching(appName, cancellationToken);
		return Manifest.ReadAll(bytes);
	}

	private async Task<byte[]> GetInstalledManifestBytes(string appName, CancellationToken cancellationToken = default)
	{
		var game = _libraryManager.GetGameInfo(appName);
		var version = game.LocalAppState?.Version;
		if (!string.IsNullOrEmpty(version))
		{
			var cached = await _storage.GetCachedManifestBytes(appName, version);
			cancellationToken.ThrowIfCancellationRequested();
			if (cached is { Length: > 0 })
				return cached;
		}
		if (version != game.AssetInfos.Windows.BuildVersion)
			throw new InvalidOperationException("The installed manifest is unavailable. Update or repair the game before managing content.");
		return await GetManifestDataWithCaching(appName, cancellationToken);
	}

	private static Manifest SelectManifest(Manifest manifest, IReadOnlyCollection<string>? tags) =>
		InstalledContentSelection.WithFiles(manifest, InstalledContentSelection.Filter(manifest.FileManifestList.Elements, tags));

	private void ValidateContentAction(InstallItem item)
	{
		if (item.ContentOwnerAppName is not { } owner)
			return;
		var game = _libraryManager.GetGameInfo(owner);
		var expectedLocation = item.Action == ActionType.Uninstall
			? _libraryManager.GetGameInfo(item.AppName).LocalAppState?.InstallPath
			: game?.LocalAppState?.InstallPath;
		if (!InstalledContentSelection.CanModify(game?.LocalAppState, _libraryManager.IsAppRunning(owner)) ||
			!string.Equals(Path.GetFullPath(item.Location), Path.GetFullPath(expectedLocation!), StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException("The game must be installed and idle to change its content.");
	}

	private async Task PrepareContentTasks(InstallItem install, Game game, Manifest manifest)
	{
		var availableTags = manifest.FileManifestList.Elements.SelectMany(file => file.InstallTags).ToHashSet(StringComparer.Ordinal);
		if (install.SelectedInstallTags is null || install.SelectedInstallTags.Except(availableTags).Any())
			throw new InvalidDataException("The content selection contains an unknown install tag.");

		var before = InstalledContentSelection.Filter(manifest.FileManifestList.Elements, game.LocalAppState!.SelectedInstallTags);
		var after = InstalledContentSelection.Filter(manifest.FileManifestList.Elements, install.SelectedInstallTags);
		var beforeNames = before.Select(file => file.Filename).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var afterNames = after.Select(file => file.Filename).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var removed = InstalledContentSelection.WithFiles(manifest, before.Where(file => !afterNames.Contains(file.Filename)));
		removed = await ExcludeSharedContent(install, removed);
		// Validate every destination before any worker can mutate files.
		foreach (var file in manifest.FileManifestList.Elements)
			InstallPathPolicy.ResolveFile(install.Location, file.Filename);
		PrepareUninstallTasks(install, removed);
		var added = after.Where(file => !beforeNames.Contains(file.Filename)).ToList();
		if (added.Count > 0)
		{
			await _downloadManager.InitializeMirrors(game.LocalAppState.BaseUrls ?? game.BaseUrls);
			GetChunksToDownloadFiltered(manifest, added);
		}
	}

	private async Task<Manifest> ExcludeSharedContent(InstallItem install, Manifest removed)
	{
		var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var state in _storage.LocalAppStateDictionary.Values.Where(state =>
			state.AppName != install.AppName && state.InstallStatus != InstallState.NotInstalled &&
			!string.IsNullOrWhiteSpace(state.InstallPath) &&
			string.Equals(Path.GetFullPath(state.InstallPath), Path.GetFullPath(install.Location), StringComparison.OrdinalIgnoreCase)).ToList())
		{
			var manifest = Manifest.ReadAll(await GetInstalledManifestBytes(state.AppName));
			foreach (var file in InstalledContentSelection.Filter(manifest.FileManifestList.Elements, state.SelectedInstallTags))
				protectedPaths.Add(InstallPathPolicy.ResolveFile(install.Location, file.Filename));
		}
		var selectedFiles = InstalledContentSelection.Filter(removed.FileManifestList.Elements,
			_libraryManager.GetGameInfo(install.AppName).LocalAppState?.SelectedInstallTags);
		return InstalledContentSelection.WithFiles(removed, selectedFiles.Where(file =>
			!protectedPaths.Contains(InstallPathPolicy.ResolveFile(install.Location, file.Filename))));
	}

	private void CancelRemainingContentActions(InstallItem install)
	{
		if (install.ContentOwnerAppName is { } owner)
			_installQueue.RemoveAll(item => item.ContentOwnerAppName == owner);
	}
}
