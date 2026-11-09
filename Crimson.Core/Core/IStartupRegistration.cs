using System.Threading.Tasks;

namespace Crimson.Core;

/// <summary>
/// Registers Crimson with Windows to start when the user signs in.
/// </summary>
public interface IStartupRegistration
{
	/// <summary>
	/// Returns whether the change took effect. Enabling returns false when Windows blocks it,
	/// for example after the user turned Crimson off in the Windows startup apps list.
	/// </summary>
	Task<bool> SetEnabledAsync(bool enabled);

	/// <summary>
	/// Returns whether Crimson will start with Windows, or null if Windows could not be asked.
	/// </summary>
	Task<bool?> IsEnabledAsync();
}
