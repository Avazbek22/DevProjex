namespace DevProjex.Application.Secrets;

/// <summary>
/// Identifies an enabled redaction operation. A null context is the deliberate fast path:
/// no detector is invoked and existing output remains byte-for-byte unchanged.
/// </summary>
[Flags]
public enum SecretRedactionFeatures : byte
{
	None = 0,
	Secrets = 1 << 0,
	PrivateData = 1 << 1
}

public static class SecretRedactionFeatureSelection
{
	public static SecretRedactionFeatures Resolve(bool hideSecrets, bool hidePrivateData)
	{
		var features = SecretRedactionFeatures.None;
		if (hideSecrets)
			features |= SecretRedactionFeatures.Secrets;
		if (hidePrivateData)
			features |= SecretRedactionFeatures.PrivateData;
		return features;
	}
}

public sealed record SecretRedactionContext(
	string ProjectRoot,
	SecretRedactionSession Session,
	SecretRedactionFeatures Features = SecretRedactionFeatures.Secrets)
{
	public Task EnsureWarmUpAsync(CancellationToken cancellationToken) =>
		Session.EnsureWarmUpAsync(Features, cancellationToken);

	public SecretRedactionScope BeginOutput(
		IReadOnlyList<string> orderedFilePaths,
		string transformIdentity = "") =>
		Session.BeginOutput(ProjectRoot, orderedFilePaths, transformIdentity, Features);

	public SecretRedactionScope BeginOutput(
		IReadOnlyList<string> orderedFilePaths,
		CancellationToken cancellationToken,
		string transformIdentity = "") =>
		Session.BeginOutput(
			ProjectRoot,
			ContentSelectionSnapshot.CreateWithCancellation(
				ProjectRoot,
				orderedFilePaths,
				cancellationToken),
			transformIdentity,
			Features);

	public SecretRedactionScope BeginOutput(
		ContentSelectionSnapshot selection,
		string transformIdentity = "") =>
		Session.BeginOutput(ProjectRoot, selection, transformIdentity, Features);
}
