using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Crimson.Models;

namespace Crimson.ViewModels;

public sealed partial class LibraryItem : ObservableObject
{
	public string Name { get; init; } = string.Empty;
	public string Title { get; init; } = string.Empty;
	public string? ImageUrl { get; init; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsInstalled))]
	[NotifyPropertyChangedFor(nameof(IsInstalling))]
	[NotifyPropertyChangedFor(nameof(ColorReveal))]
	private InstallState _installState;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ColorReveal))]
	private double _installProgress;

	public bool IsInstalled => InstallState is InstallState.Installed or InstallState.Playing or InstallState.NeedUpdate;

	public bool IsInstalling => InstallState is InstallState.Installing or InstallState.InstallationPaused
		or InstallState.Updating or InstallState.UpdatingPaused or InstallState.Repairing;

	public double ColorReveal => IsInstalled
		? 1
		: IsInstalling
			? Math.Clamp(InstallProgress / 100d, 0d, 1d)
			: 0;
}
