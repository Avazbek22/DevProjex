namespace DevProjex.Infrastructure.SmartIgnore;

/// <summary>
/// Smart ignore rule for Java/Kotlin/Gradle build output folders.
/// Activates when Maven/Gradle markers are present in the scope root.
/// </summary>
public sealed class JvmArtifactsIgnoreRule : ISmartIgnoreRule, ISmartIgnoreRuleDescriptorProvider
{
	private static readonly IReadOnlySet<string> MarkerFiles = SmartIgnoreRuleSet.Create(
		"pom.xml",
		"build.gradle",
		"build.gradle.kts",
		"settings.gradle",
		"settings.gradle.kts");

	private static readonly IReadOnlySet<string> FolderNames = SmartIgnoreRuleSet.Create(
		"target",
		".gradle",
		"build",
		"out");

	private static readonly SmartIgnoreResult MatchResult =
		SmartIgnoreRuleSet.Result(folderNames: FolderNames);

	public SmartIgnoreRuleDescriptor Descriptor { get; } =
		SmartIgnoreRuleSet.Descriptor(markerFiles: MarkerFiles, folderNames: FolderNames);

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
