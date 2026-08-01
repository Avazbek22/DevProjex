namespace DevProjex.Infrastructure.SmartIgnore;

/// <summary>
/// Smart ignore rule for Python cache and virtual environment folders.
/// Activates only when Python project markers are detected in the scope root.
/// </summary>
public sealed class PythonArtifactsIgnoreRule :
	ISmartIgnoreRule,
	IProjectRootFactsSmartIgnoreRule,
	ISmartIgnoreRuleDescriptorProvider
{
	private static readonly IReadOnlySet<string> MarkerFiles = SmartIgnoreRuleSet.Create(
		"pyproject.toml",
		"requirements.txt",
		"requirements-dev.txt",
		"setup.py",
		"setup.cfg",
		"Pipfile",
		"poetry.lock",
		"environment.yml");

	private static readonly IReadOnlySet<string> FolderNames = SmartIgnoreRuleSet.Create(
		"__pycache__",
		".pytest_cache",
		".mypy_cache",
		".ruff_cache",
		".tox",
		".nox",
		".venv",
		"venv",
		"env",
		".hypothesis",
		".ipynb_checkpoints",
		".pyre");
	private static readonly IReadOnlySet<string> EvidenceRequiredFolderNames =
		SmartIgnoreRuleSet.Create("venv", "env");

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
