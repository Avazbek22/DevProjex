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
		return await planner
			.BuildAsync(
				new ProjectContextRequest(projectPath, selection, sourceIdentity),
				cancellationToken)
			.ConfigureAwait(false);
	}
}
