namespace DevProjex.Infrastructure.SmartIgnore;

/// <summary>
/// Smart ignore rule for Rust build output folders.
/// Activates when Cargo.toml exists in the scope root.
/// </summary>
public sealed class RustArtifactsIgnoreRule :
	ISmartIgnoreRule,
	IProjectRootFactsSmartIgnoreRule,
	ISmartIgnoreRuleDescriptorProvider
{
	private const string MarkerFile = "Cargo.toml";

	private static readonly IReadOnlySet<string> MarkerFiles = SmartIgnoreRuleSet.Create(MarkerFile);

	private static readonly IReadOnlySet<string> FolderNames = SmartIgnoreRuleSet.Create("target");

	private static readonly SmartIgnoreResult MatchResult =
		SmartIgnoreRuleSet.Result(folderNames: FolderNames);

	public SmartIgnoreRuleDescriptor Descriptor { get; } =
		SmartIgnoreRuleSet.Descriptor(markerFiles: MarkerFiles, folderNames: FolderNames);

	public SmartIgnoreResult Evaluate(ProjectRootFacts rootFacts) =>
		rootFacts.Exists && rootFacts.HasMarkerFile(MarkerFile)
			? MatchResult
			: SmartIgnoreResult.Empty;

	public SmartIgnoreResult Evaluate(string rootPath)
	{
		if (!Directory.Exists(rootPath))
			return SmartIgnoreResult.Empty;

		if (!File.Exists(Path.Combine(rootPath, MarkerFile)))
			return SmartIgnoreResult.Empty;

		return MatchResult;
	}
}
