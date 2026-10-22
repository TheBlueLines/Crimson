using System;
using System.Collections.Generic;
using System.Linq;
using Crimson.Models;

namespace Crimson.Core;

public static class InstalledContentSelection
{
	// Null preserves the legacy installation of every tagged file. Empty means base files only.
	public static List<FileManifest> Filter(IEnumerable<FileManifest> files, IReadOnlyCollection<string>? tags) =>
		files.Where(file => tags is null || file.InstallTags.Count == 0 ||
			file.InstallTags.Contains(string.Empty) || file.InstallTags.Any(tags.Contains)).ToList();

	public static (List<string> Added, List<string> Removed) Diff(
		IEnumerable<string> installed, IEnumerable<string> selected)
	{
		var before = installed.ToHashSet(StringComparer.Ordinal);
		var after = selected.ToHashSet(StringComparer.Ordinal);
		return (after.Except(before).ToList(), before.Except(after).ToList());
	}

	public static bool CanModify(LocalAppState? state, bool hasPendingAction) =>
		state?.InstallStatus is InstallState.Installed or InstallState.NeedUpdate &&
		!string.IsNullOrWhiteSpace(state.InstallPath) && !hasPendingAction;

	internal static Manifest WithFiles(Manifest manifest, IEnumerable<FileManifest> files)
	{
		var result = new Manifest
		{
			ManifestMeta = manifest.ManifestMeta,
			CDL = manifest.CDL,
			CustomFields = manifest.CustomFields,
			FileManifestList = new FileManifestList()
		};
		result.FileManifestList.Elements.AddRange(files);
		return result;
	}
}
