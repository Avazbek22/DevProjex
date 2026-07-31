namespace DevProjex.Infrastructure.SmartIgnore;

/// <summary>
/// Smart ignore rule for .NET build artifacts (bin, obj folders).
/// Activates when .sln, .csproj, .fsproj, or .vbproj files are found.
/// </summary>
public sealed class DotNetArtifactsIgnoreRule :
	ISmartIgnoreRule,
	IProjectRootFactsSmartIgnoreRule,
	ISmartIgnoreRuleDescriptorProvider
{
	private static readonly IReadOnlySet<string> MarkerExtensions = SmartIgnoreRuleSet.Create(
		".sln",
		".csproj",
		".fsproj",
		".vbproj");

	private static readonly IReadOnlySet<string> FolderNames = SmartIgnoreRuleSet.Create(
		"bin",
		"obj");
	private static readonly IReadOnlySet<string> EvidenceRequiredFolderNames = FolderNames;

	private static readonly SmartIgnoreResult MatchResult =
		SmartIgnoreRuleSet.Result(folderNames: FolderNames);

	public SmartIgnoreRuleDescriptor Descriptor { get; } =
		SmartIgnoreRuleSet.Descriptor(
			markerExtensions: MarkerExtensions,
			folderNames: FolderNames,
			evidenceRequiredFolderNames: EvidenceRequiredFolderNames);

	public SmartIgnoreResult Evaluate(ProjectRootFacts rootFacts) =>
		rootFacts.Exists && rootFacts.HasAnyFileExtension(MarkerExtensions)
			? MatchResult
			: SmartIgnoreResult.Empty;

	public SmartIgnoreResult Evaluate(string rootPath)
	{
		if (!Directory.Exists(rootPath))
			return SmartIgnoreResult.Empty;

		bool hasMarker;
		try
		{
			hasMarker = HasAnyMarkerExtension(rootPath);
		}
		catch
		{
			return SmartIgnoreResult.Empty;
		}

		if (!hasMarker)
			return SmartIgnoreResult.Empty;

		return MatchResult;
	}

	private static bool HasAnyMarkerExtension(string rootPath)
	{
		foreach (var filePath in Directory.EnumerateFiles(rootPath, "*", SearchOption.TopDirectoryOnly))
		{
			var extension = Path.GetExtension(filePath);
			if (!string.IsNullOrWhiteSpace(extension) && MarkerExtensions.Contains(extension))
				return true;
		}

		return false;
	}
}
