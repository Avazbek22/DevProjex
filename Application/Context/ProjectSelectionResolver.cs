using DevProjex.Application.Selection;

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

		var resolved = baseline with
		{
			Roots = overrides.Roots ?? baseline.Roots,
			Extensions = overrides.Extensions ?? baseline.Extensions,
			SelectedPaths = overrides.SelectedPaths ?? baseline.SelectedPaths,
			GitMode = overrides.GitMode ?? baseline.GitMode,
			Exclusions = overrides.Exclusions ?? baseline.Exclusions,
			ProfileSource = profile
		};

		if (baseline.LocalProfileState is { } localState)
		{
			resolved = resolved with
			{
				LocalProfileState = localState with
				{
					RootsOverridden = overrides.Roots is not null,
					ExtensionsOverridden = overrides.Extensions is not null,
					IgnoreOptionsOverridden = overrides.GitMode is not null || overrides.Exclusions is not null
				}
			};
		}

		return resolved;
	}

	private ProjectSelectionSpec ResolveLocal(string projectPath)
	{
		if (!localProfileStore.TryLoadProfile(projectPath, out var profile))
		{
			throw new ProjectContextValidationException(
				"DPX-CLI-PROFILE-NOT-FOUND",
				"No local profile exists for this project.");
		}

		var snapshot = ProjectSelectionProfileBuilder.Clone(profile);
		return ProjectSelectionAdapter.FromLegacyProfile(snapshot, ProjectProfileReference.Local) with
		{
			LocalProfileState = new LocalProjectSelectionState(snapshot)
		};
	}
}
