namespace DevProjex.Infrastructure.SmartIgnore;

/// <summary>
/// Smart ignore rule for Swift package-manager and Apple-platform build artifacts.
/// Activates only inside a scope marked by a Swift dependency manifest.
/// </summary>
public sealed class SwiftArtifactsIgnoreRule :
	ISmartIgnoreRule,
	IProjectRootFactsSmartIgnoreRule,
	ISmartIgnoreRuleDescriptorProvider
{
	private static readonly IReadOnlySet<string> MarkerFiles = SmartIgnoreRuleSet.Create(
		"Package.swift",
		"Podfile",
		"Cartfile");

	private static readonly IReadOnlySet<string> FolderNames = SmartIgnoreRuleSet.Create(
		".build",
		"DerivedData",
		"Pods",
		"Carthage");
	private static readonly IReadOnlySet<string> EvidenceRequiredFolderNames =
		SmartIgnoreRuleSet.Create(".build", "DerivedData", "Pods", "Carthage");

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
