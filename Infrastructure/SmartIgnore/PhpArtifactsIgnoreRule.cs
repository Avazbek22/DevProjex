namespace DevProjex.Infrastructure.SmartIgnore;

/// <summary>
/// Smart ignore rule for Composer dependency folders.
/// Activates when composer.json exists in the scope root.
/// </summary>
public sealed class PhpArtifactsIgnoreRule : ISmartIgnoreRule, ISmartIgnoreRuleDescriptorProvider
{
	private static readonly string[] MarkerFiles =
	[
		"composer.json"
	];

	private static readonly string[] FolderNames =
	[
		"vendor"
	];

	public SmartIgnoreRuleDescriptor Descriptor { get; } = new(
		new HashSet<string>(MarkerFiles, StringComparer.OrdinalIgnoreCase),
		new HashSet<string>(StringComparer.OrdinalIgnoreCase),
		new HashSet<string>(FolderNames, StringComparer.OrdinalIgnoreCase),
		new HashSet<string>(StringComparer.OrdinalIgnoreCase));

	public SmartIgnoreResult Evaluate(string rootPath)
	{
		if (!Directory.Exists(rootPath))
			return new SmartIgnoreResult(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase),
				new HashSet<string>(StringComparer.OrdinalIgnoreCase));

		if (!File.Exists(Path.Combine(rootPath, MarkerFiles[0])))
			return new SmartIgnoreResult(
				new HashSet<string>(StringComparer.OrdinalIgnoreCase),
				new HashSet<string>(StringComparer.OrdinalIgnoreCase));

		return new SmartIgnoreResult(
			new HashSet<string>(FolderNames, StringComparer.OrdinalIgnoreCase),
			new HashSet<string>(StringComparer.OrdinalIgnoreCase));
	}
}
