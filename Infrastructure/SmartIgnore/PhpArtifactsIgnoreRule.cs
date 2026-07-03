namespace DevProjex.Infrastructure.SmartIgnore;

/// <summary>
/// Smart ignore rule for Composer dependency folders.
/// Activates when composer.json exists in the scope root.
/// </summary>
public sealed class PhpArtifactsIgnoreRule : ISmartIgnoreRule, ISmartIgnoreRuleDescriptorProvider
{
	private const string MarkerFile = "composer.json";

	private static readonly IReadOnlySet<string> MarkerFiles = SmartIgnoreRuleSet.Create(MarkerFile);

	private static readonly IReadOnlySet<string> FolderNames = SmartIgnoreRuleSet.Create("vendor");

	private static readonly SmartIgnoreResult MatchResult =
		SmartIgnoreRuleSet.Result(folderNames: FolderNames);

	public SmartIgnoreRuleDescriptor Descriptor { get; } =
		SmartIgnoreRuleSet.Descriptor(markerFiles: MarkerFiles, folderNames: FolderNames);

	public SmartIgnoreResult Evaluate(string rootPath)
	{
		if (!Directory.Exists(rootPath))
			return SmartIgnoreResult.Empty;

		if (!File.Exists(Path.Combine(rootPath, MarkerFile)))
			return SmartIgnoreResult.Empty;

		return MatchResult;
	}
}
