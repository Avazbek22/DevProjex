using DevProjex.Application.Secrets;

namespace DevProjex.Terminal.Execution;

public sealed class TerminalProjectContextFactory(
	ProjectContextPlanner planner,
	ProjectSourceIdentityResolver sourceIdentityResolver,
	SecretRedactionSession secretRedactionSession)
{
	public async Task<ProjectContextPlan> BuildAsync(
		string projectPath,
		ProjectSelectionSpec selection,
		ProjectSourceIdentity? knownIdentity = null,
		CancellationToken cancellationToken = default)
	{
		secretRedactionSession.ReplaceMarkedSecrets(
			ProjectSelectionMarkedSecretsResolver.Resolve(selection));
		var sourceIdentity = await sourceIdentityResolver
			.ResolveAsync(projectPath, knownIdentity, cancellationToken)
			.ConfigureAwait(false);
		return await planner
			.BuildAsync(
				new ProjectContextRequest(projectPath, selection, sourceIdentity),
				cancellationToken)
			.ConfigureAwait(false);
	}
}
