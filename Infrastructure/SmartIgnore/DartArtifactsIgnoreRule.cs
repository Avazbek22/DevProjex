namespace DevProjex.Infrastructure.SmartIgnore;

/// <summary>
/// Smart ignore rule for Dart and Flutter generated state and build output.
/// Activates when a pub manifest or lock file exists in the scope root.
/// </summary>
public sealed class DartArtifactsIgnoreRule :
	ISmartIgnoreRule,
	IProjectRootFactsSmartIgnoreRule,
	ISmartIgnoreRuleDescriptorProvider
{
	private static readonly IReadOnlySet<string> MarkerFiles = SmartIgnoreRuleSet.Create(
		"pubspec.yaml",
		"pubspec.lock");

	private static readonly IReadOnlySet<string> FolderNames = SmartIgnoreRuleSet.Create(
		".dart_tool",
		"build");
	private static readonly IReadOnlySet<string> EvidenceRequiredFolderNames =
		SmartIgnoreRuleSet.Create(".dart_tool", "build");

	private static readonly SmartIgnoreResult MatchResult =
		SmartIgnoreRuleSet.Result(folderNames: FolderNames);

	public SmartIgnoreRuleDescriptor Descriptor { get; } =
		SmartIgnoreRuleSet.Descriptor(
			markerFiles: MarkerFiles,
			folderNames: FolderNames,
			evidenceRequiredFolderNames: EvidenceRequiredFolderNames);

	public SmartIgnoreResult Evaluate(ProjectRootFacts rootFacts) =>
		rootFacts.Exists && rootFacts.HasAnyMarkerFile(MarkerFiles)
			? MatchResult
			: SmartIgnoreResult.Empty;

	public SmartIgnoreResult Evaluate(string rootPath)
	{
		if (!Directory.Exists(rootPath))
			return SmartIgnoreResult.Empty;

		foreach (var marker in MarkerFiles)
		{
			if (File.Exists(Path.Combine(rootPath, marker)))
				return MatchResult;
		}

		return SmartIgnoreResult.Empty;
	}
}
