namespace Crimson.Models;

class Settings
{
	public int Theme { get; set; } = 0;

	public bool AcrylicEnabled { get; set; } = true;

	public string DefaultInstallLocation { get; set; }

	public bool CloudSaveAutoSync { get; set; }

	public CloseBehavior CloseBehavior { get; set; } = CloseBehavior.Exit;
}
