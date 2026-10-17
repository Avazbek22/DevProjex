using DevProjex.Application.Updates;

namespace DevProjex.Infrastructure.Updates;

public sealed class FlathubApplicationUpdateService : IApplicationUpdateService
{
	public Task<ApplicationUpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default) =>
		Task.FromResult(new ApplicationUpdateCheckResult(ApplicationUpdateAvailability.CheckFailed, currentVersion));
}
