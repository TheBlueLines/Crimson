using Crimson.Models;

namespace Crimson.ViewModels;

public sealed class DownloadManagerItem
{
	public string Name { get; init; } = string.Empty;
	public string Title { get; init; } = string.Empty;
	public string? ImageUrl { get; init; }
	public InstallState InstallState { get; init; }
}
