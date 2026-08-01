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
		var applyProfileValues = profile.Kind != ProjectProfileSourceKind.Local;
		resolved = resolved with
		{
			// A local profile is loaded by Desktop with its complete option-state maps. Only
			// explicit command-line components may replace that live profile state. Standard
			// and portable profiles, in contrast, must cross the Desktop boundary themselves.
			ApplicationIntent = new ProjectSelectionApplicationIntent(
				Roots: ResolveApplicationMode(overrides.Roots is not null, applyProfileValues, resolved.Roots),
				Extensions: ResolveApplicationMode(
					overrides.Extensions is not null,
					applyProfileValues,
					resolved.Extensions),
				GitMode: ResolveApplicationMode(
					overrides.GitMode is not null,
					applyProfileValues,
					resolved.GitMode),
				Exclusions: ResolveApplicationMode(
					overrides.Exclusions is not null,
					applyProfileValues,
					resolved.Exclusions))
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

	private static ProjectSelectionApplicationMode ResolveApplicationMode<T>(
		bool hasExplicitOverride,
		bool applyProfileValues,
		T? resolvedValue)
	{
		if (hasExplicitOverride)
			return ProjectSelectionApplicationMode.ApplyResolvedValue;
		if (!applyProfileValues)
			return ProjectSelectionApplicationMode.Preserve;

		return resolvedValue is null
			? ProjectSelectionApplicationMode.ResetToDefaults
			: ProjectSelectionApplicationMode.ApplyResolvedValue;
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
