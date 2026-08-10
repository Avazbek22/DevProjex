namespace DevProjex.Application.Secrets;

/// <summary>
/// Identifies an enabled redaction operation. A null context is the deliberate fast path:
/// no detector is invoked and existing output remains byte-for-byte unchanged.
/// </summary>
public sealed record SecretRedactionContext(
	string ProjectRoot,
	SecretRedactionSession Session)
{
	public SecretRedactionScope BeginOutput(
		IReadOnlyList<string> orderedFilePaths,
		string transformIdentity = "") =>
		Session.BeginOutput(ProjectRoot, orderedFilePaths, transformIdentity);

	public SecretRedactionScope BeginOutput(
		ContentSelectionSnapshot selection,
		string transformIdentity = "") =>
		Session.BeginOutput(ProjectRoot, selection, transformIdentity);
}
