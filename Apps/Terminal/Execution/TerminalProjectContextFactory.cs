using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.Git;

namespace DevProjex.Terminal.Execution;

public sealed class TerminalProjectContextFactory(
	ProjectContextPlanner planner,
	ProjectSourceIdentityResolver sourceIdentityResolver,
	SecretRedactionSession secretRedactionSession,
	IGitScopePathProvider gitScopePathProvider,
	GitRemoteDiffRangeResolver remoteDiffRangeResolver)
{
	public Task<ProjectContextPlan> BuildAsync(
		string projectPath,
		ProjectSelectionSpec selection,
		ProjectSourceIdentity? knownIdentity = null,
		CancellationToken cancellationToken = default,
		bool captureIgnoreImpactCounts = false,
		IReadOnlyDictionary<string, bool>? knownExtensionStates = null,
		IReadOnlyCollection<string>? repositoryScopeFullPaths = null,
		string? repositorySourceUrl = null)
		=> BuildAsync(
			projectPath,
			selection,
			includeOutputMetrics: true,
			knownIdentity,
			cancellationToken,
			captureIgnoreImpactCounts,
			includeContentOutputMetrics: true,
			knownExtensionStates,
			repositoryScopeFullPaths,
			repositorySourceUrl);

	internal async Task<ProjectContextPlan> BuildAsync(
		string projectPath,
		ProjectSelectionSpec selection,
		bool includeOutputMetrics,
		ProjectSourceIdentity? knownIdentity = null,
		CancellationToken cancellationToken = default,
		bool captureIgnoreImpactCounts = false,
		bool includeContentOutputMetrics = true,
		IReadOnlyDictionary<string, bool>? knownExtensionStates = null,
		IReadOnlyCollection<string>? repositoryScopeFullPaths = null,
		string? repositorySourceUrl = null)
	{
		var markedSecrets = ProjectSelectionMarkedSecretsResolver.Resolve(selection);
		if (await secretRedactionSession
			    .EnsurePersistentIdentityReadyAsync(markedSecrets, cancellationToken)
			    .ConfigureAwait(false) != PersistentSecretIdentityAvailability.Ready)
		{
			throw new SecretDetectionException("The persistent secret identity key is unavailable.");
		}
		if (selection.ProfileSource?.Kind == ProjectProfileSourceKind.Local)
		{
			secretRedactionSession.ReplacePersistentMarks(
				projectPath,
				new PersistentSecretMarksSnapshot(0, markedSecrets));
		}
		else
		{
			secretRedactionSession.ReplaceMarkedSecrets(markedSecrets);
		}
		var sourceIdentity = await sourceIdentityResolver
			.ResolveAsync(projectPath, knownIdentity, cancellationToken)
			.ConfigureAwait(false);
		string? resolvedDiffRange = null;
		if (sourceIdentity.SourceType == ProjectSourceType.GitClone &&
		    selection.GitMode == GitFilteringMode.Diff &&
		    selection.GitDiffRange is { } diffRange)
		{
			var sourceUrl = repositorySourceUrl ?? sourceIdentity.RepositoryUrl;
			if (!string.IsNullOrWhiteSpace(sourceUrl))
			{
				resolvedDiffRange = await remoteDiffRangeResolver.ResolveAsync(
					projectPath,
					sourceUrl,
					diffRange,
					sourceIdentity.Branch,
					cancellationToken).ConfigureAwait(false);
			}
		}
		var request = new ProjectContextRequest(projectPath, selection, sourceIdentity)
		{
			KnownExtensionStates = knownExtensionStates
		};
		ProjectContextPlan plan;
		if (!includeOutputMetrics)
		{
			plan = await planner
				.BuildStructureAsync(request, cancellationToken)
				.ConfigureAwait(false);
		}
		else if (!includeContentOutputMetrics)
		{
			plan = await planner
				.BuildWithTreeMetricsAsync(request, cancellationToken)
				.ConfigureAwait(false);
		}
		else if (captureIgnoreImpactCounts)
		{
			plan = await planner
				.BuildWithIgnoreImpactCountsAsync(request, cancellationToken)
				.ConfigureAwait(false);
		}
		else
		{
			plan = await planner.BuildAsync(request, cancellationToken).ConfigureAwait(false);
		}

		return await GitScopeFilter
			.ApplyAsync(
				planner,
				plan,
				gitScopePathProvider,
				plan.Selection.GitMode ?? GitFilteringMode.None,
				plan.Selection.GitDiffRange,
				repositoryScopeFullPaths,
				cancellationToken,
				resolvedDiffRange)
			.ConfigureAwait(false);
	}
}
