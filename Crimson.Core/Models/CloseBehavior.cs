namespace Crimson.Models;

/// <summary>
/// What happens when the main window is closed. The values are saved in the settings file and
/// are the indexes of the options on the Settings page.
/// </summary>
public enum CloseBehavior
{
	/// <summary>Exits the app.</summary>
	Exit = 0,

	/// <summary>Hides the window and keeps running. The tray icon only appears while hidden.</summary>
	MinimizeToTray = 1,

	/// <summary>Hides the window and keeps running. The tray icon is always shown.</summary>
	RunInBackground = 2
}
