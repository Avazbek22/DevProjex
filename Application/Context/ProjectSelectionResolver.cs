namespace DevProjex.Application.Context;

public sealed class ProjectSelectionResolver(
	IProjectProfileStore localProfileStore,
	Func<string, CancellationToken, Task<ProjectSelectionSpec>> portableProfileLoader)
{
	public async Task<ProjectSelectionSpec> ResolveAsync(
		string projectPath,
		ProjectProfileReference profile,
		ProjectSelectionSpec overrides,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(profile);
		ArgumentNullException.ThrowIfNull(overrides);

		var baseline = profile.Kind switch
		{
			ProjectProfileSourceKind.Standard => ProjectSelectionSpec.Standard,
			ProjectProfileSourceKind.Local => ResolveLocal(projectPath),
			ProjectProfileSourceKind.Portable => await portableProfileLoader(
				profile.Path ?? string.Empty,
				cancellationToken).ConfigureAwait(false),
			_ => throw new ArgumentOutOfRangeException(nameof(profile), profile.Kind, null)
		};

		return baseline with
		{
			Roots = overrides.Roots ?? baseline.Roots,
			Extensions = overrides.Extensions ?? baseline.Extensions,
			SelectedPaths = overrides.SelectedPaths ?? baseline.SelectedPaths,
			GitMode = overrides.GitMode ?? baseline.GitMode,
			Exclusions = overrides.Exclusions ?? baseline.Exclusions,
			ProfileSource = profile
		};
	}

	private ProjectSelectionSpec ResolveLocal(string projectPath)
	{
		if (!localProfileStore.TryLoadProfile(projectPath, out var profile))
		{
			throw new ProjectContextValidationException(
				"DPX-CLI-PROFILE-NOT-FOUND",
				"No local profile exists for this project.");
		}

		return ProjectSelectionAdapter.FromLegacyProfile(profile, ProjectProfileReference.Local);
	}
}
