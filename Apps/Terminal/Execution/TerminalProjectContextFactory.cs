using System.Runtime.CompilerServices;
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
	// Keyed by the plan BuildAsync returns, before any later filter (such as
	// ProjectFileSizeFilter) produces a new plan instance that would not be found here.
	private readonly ConditionalWeakTable<ProjectContextPlan, string> resolvedDiffRanges = new();

	/// <summary>
	/// The commit range <paramref name="plan"/>'s <c>diff:&lt;A&gt;..&lt;B&gt;</c> scope was resolved
	/// to against its remote, when it was a Git URL source and resolution ran; null for a local
	/// project or when no resolution was needed. Call before any filter replaces the plan instance.
	/// </summary>
	public string? TryGetResolvedDiffRange(ProjectContextPlan plan) =>
		resolvedDiffRanges.TryGetValue(plan, out var resolved) ? resolved : null;

	/// <summary>
	/// Resolves <paramref name="diffRange"/> against <paramref name="plan"/>'s own remote, for a
	/// momentary Git scope applied to a plan built without one: the same resolution
	/// <see cref="BuildAsync"/> performs up front, usable once the range is only known afterward.
	/// Returns null for a local project, one without a remote URL, or when resolution fails.
	/// </summary>
	public Task<string?> ResolveDiffRangeAsync(
		ProjectContextPlan plan,
		string diffRange,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentException.ThrowIfNullOrWhiteSpace(diffRange);
		var identity = plan.SourceIdentity;
		if (identity is null || identity.SourceType != ProjectSourceType.GitClone)
			return Task.FromResult<string?>(null);
		var sourceUrl = identity.RepositoryUrl;
		return string.IsNullOrWhiteSpace(sourceUrl)
			? Task.FromResult<string?>(null)
			: remoteDiffRangeResolver.ResolveAsync(
				plan.SourceRoot,
				sourceUrl,
				diffRange,
				identity.Branch,
				cancellationToken);
	}

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
		string? repositorySourceUrl = null,
		bool applyMarkedSecrets = true)
	{
		var markedSecrets = ProjectSelectionMarkedSecretsResolver.Resolve(selection).ToArray();
		if (await secretRedactionSession
			    .EnsurePersistentIdentityReadyAsync(markedSecrets, cancellationToken)
			    .ConfigureAwait(false) != PersistentSecretIdentityAvailability.Ready)
		{
			throw new SecretDetectionException("The persistent secret identity key is unavailable.");
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
				.BuildWithTreeMetricsAsync(request, captureIgnoreImpactCounts, cancellationToken)
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

		plan = await GitScopeFilter
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
		cancellationToken.ThrowIfCancellationRequested();
		if (applyMarkedSecrets)
			ApplyMarkedSecretsCore(projectPath, selection, markedSecrets);
		if (resolvedDiffRange is not null)
			resolvedDiffRanges.AddOrUpdate(plan, resolvedDiffRange);
		return plan;
	}

	internal void ApplyMarkedSecrets(string projectPath, ProjectSelectionSpec selection) =>
		ApplyMarkedSecretsCore(
			projectPath,
			selection,
			ProjectSelectionMarkedSecretsResolver.Resolve(selection).ToArray());

	private void ApplyMarkedSecretsCore(
		string projectPath,
		ProjectSelectionSpec selection,
		IReadOnlyCollection<MarkedSecretProfileEntry> markedSecrets)
	{
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
	}
}
