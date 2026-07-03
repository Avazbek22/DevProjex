namespace DevProjex.Infrastructure.SmartIgnore;

public sealed class FrontendArtifactsIgnoreRule : ISmartIgnoreRule, ISmartIgnoreRuleDescriptorProvider
{
	private static readonly IReadOnlySet<string> MarkerFiles = SmartIgnoreRuleSet.Create(
		"package.json",
		"package-lock.json",
		"pnpm-lock.yaml",
		"yarn.lock",
		"bun.lockb",
		"bun.lock",
		"pnpm-workspace.yaml",
		"npm-shrinkwrap.json");

	private static readonly IReadOnlySet<string> FolderNames = SmartIgnoreRuleSet.Create(
		"node_modules",
		"dist",
		"build",
		".next",
		".nuxt",
		".turbo",
		".svelte-kit",
		".angular",
		"coverage",
		".cache",
		".parcel-cache",
		".vite",
		".output",
		".astro",
		"storybook-static",
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
