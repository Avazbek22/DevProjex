namespace DevProjex.Infrastructure.SmartIgnore;

/// <summary>
/// Smart ignore rule for Ruby/Rails generated and dependency folders.
/// Activates when Gemfile or Gemfile.lock exists in the scope root.
/// </summary>
public sealed class RubyArtifactsIgnoreRule :
	ISmartIgnoreRule,
	IProjectRootFactsSmartIgnoreRule,
	ISmartIgnoreRuleDescriptorProvider
{
	private static readonly IReadOnlySet<string> MarkerFiles = SmartIgnoreRuleSet.Create(
		"Gemfile",
		"Gemfile.lock");

	private static readonly IReadOnlySet<string> FolderNames = SmartIgnoreRuleSet.Create(
		".bundle",
		"vendor",
		"log",
		"tmp");

	private static readonly SmartIgnoreResult MatchResult =
		SmartIgnoreRuleSet.Result(folderNames: FolderNames);

	public SmartIgnoreRuleDescriptor Descriptor { get; } =
		SmartIgnoreRuleSet.Descriptor(markerFiles: MarkerFiles, folderNames: FolderNames);

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
