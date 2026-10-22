namespace Crimson.Core;

public sealed record InstallPermissionCheckResult(
	bool CanWrite,
	string? ErrorType = null,
	string? CleanupErrorType = null);

public interface IInstallPermissionChecker
{
	InstallPermissionCheckResult Check(string folderPath);
}
