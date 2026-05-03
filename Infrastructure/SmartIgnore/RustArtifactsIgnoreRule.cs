namespace DevProjex.Infrastructure.SmartIgnore;

/// <summary>
/// Smart ignore rule for Rust build output folders.
/// Activates when Cargo.toml exists in the scope root.
/// </summary>
public sealed class RustArtifactsIgnoreRule : ISmartIgnoreRule, ISmartIgnoreRuleDescriptorProvider
{
	private static readonly string[] MarkerFiles =
	[
		"Cargo.toml"
	];

	private static readonly string[] FolderNames =
	[
		"target"
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
