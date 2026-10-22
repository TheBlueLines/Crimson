using CommunityToolkit.Mvvm.ComponentModel;

namespace Crimson.Models;

/// <summary>
/// Represents a selectable DLC item in the install dialog
/// </summary>
public partial class DlcOption : ObservableObject
{
	public string AppName { get; set; }
	public string Title { get; set; }

	[ObservableProperty]
	private bool _isSelected;
}
