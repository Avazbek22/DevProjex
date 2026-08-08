using DevProjex.Kernel.Models;

namespace DevProjex.Application.Context;

public static class ProjectSelectionMarkedSecretsResolver
{
	public static IReadOnlyCollection<MarkedSecretProfileEntry> Resolve(
		ProjectSelectionSpec selection)
	{
		ArgumentNullException.ThrowIfNull(selection);
		return selection.LocalProfileState?.Profile.MarkedSecrets ?? [];
	}
}
