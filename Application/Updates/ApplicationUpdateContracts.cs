namespace DevProjex.Application.Updates;

public enum ApplicationUpdateAvailability
{
    CheckFailed,
    UpToDate,
    UpdateAvailable,
    CurrentVersionNewer
}

public sealed record ApplicationUpdateCheckResult(
    ApplicationUpdateAvailability Availability,
    string CurrentVersion,
    string? LatestVersion = null);

public interface IApplicationUpdateService
{
    Task<ApplicationUpdateCheckResult> CheckAsync(
        string currentVersion,
        CancellationToken cancellationToken = default);
}
