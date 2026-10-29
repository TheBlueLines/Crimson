namespace Crimson.ViewModels;

public sealed class PausedDownloadItem
{
	public string Name { get; init; } = string.Empty;
	public string Title { get; init; } = string.Empty;
	public string? ImageUrl { get; init; }
	public int ProgressPercentage { get; init; }
	public string SizeText { get; init; } = string.Empty;
}
