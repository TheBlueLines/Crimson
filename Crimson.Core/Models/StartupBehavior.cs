namespace Crimson.Models;

/// <summary>
/// What happens when the user signs in to Windows. The values are saved in the settings file and
/// are the indexes of the options on the Settings page.
/// </summary>
public enum StartupBehavior
{
	/// <summary>Crimson does not start.</summary>
	None = 0,

	/// <summary>Crimson starts with its window hidden and is reached from the tray icon.</summary>
	RunInBackground = 1,

	/// <summary>Crimson starts and opens its window.</summary>
	OpenApp = 2
}
