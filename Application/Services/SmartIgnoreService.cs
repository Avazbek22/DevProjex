using System.Collections.Frozen;

namespace DevProjex.Application.Services;

public sealed class SmartIgnoreService
{
	private readonly IReadOnlyList<ISmartIgnoreRule> _rules;
	private readonly IReadOnlyList<SmartIgnoreRuleDescriptor> _descriptors;

	public SmartIgnoreService(
		IEnumerable<ISmartIgnoreRule> rules,
		ProjectRootFactsProvider? rootFactsProvider = null)
	{
		var ruleList = rules.ToList();
		_rules = ruleList;
		_descriptors = ruleList
			.OfType<ISmartIgnoreRuleDescriptorProvider>()
			.Select(provider => provider.Descriptor)
			.ToArray();
		RootFactsProvider = rootFactsProvider ?? new ProjectRootFactsProvider();
	}

	public ProjectRootFactsProvider RootFactsProvider { get; }

	public SmartIgnoreResult Build(string rootPath) =>
		Build(RootFactsProvider.Get(rootPath));

	public SmartIgnoreResult Build(ProjectRootFacts rootFacts)
	{
		var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var rule in _rules)
		{
			SmartIgnoreResult result;
			try
			{
				result = rule is IProjectRootFactsSmartIgnoreRule factsRule
					? factsRule.Evaluate(rootFacts)
					: rule.Evaluate(rootFacts.RootPath);
			}
			catch
			{
				// Fault isolation: one problematic rule or inaccessible folder
				// must not break the whole ignore pipeline.
				continue;
			}

			foreach (var folder in result.FolderNames)
				folders.Add(folder);
			foreach (var file in result.FileNames)
				files.Add(file);
		}

		if (folders.Count == 0 && files.Count == 0)
			return SmartIgnoreResult.Empty;

		return new SmartIgnoreResult(
			FreezeOrEmpty(folders, SmartIgnoreResult.Empty.FolderNames),
			FreezeOrEmpty(files, SmartIgnoreResult.Empty.FileNames));
	}

	public bool HasKnownProjectMarker(string rootPath)
	{
		if (string.IsNullOrWhiteSpace(rootPath))
			return false;

		return HasKnownProjectMarker(RootFactsProvider.Get(rootPath));
	}

	public bool HasKnownProjectMarker(ProjectRootFacts rootFacts)
	{
		if (!rootFacts.Exists)
			return false;

		if (!rootFacts.IsAccessible)
			return HasKnownProjectMarkerByTargetedProbe(rootFacts.RootPath);

		foreach (var descriptor in _descriptors)
		{
			if (rootFacts.HasAnyMarkerFile(descriptor.MarkerFiles) ||
			    rootFacts.HasAnyFileExtension(descriptor.MarkerExtensions))
			{
				return true;
			}
		}

		return false;
	}

	public bool IsKnownProjectMarker(string fileName, string? extension)
	{
		if (string.IsNullOrWhiteSpace(fileName))
			return false;

		foreach (var descriptor in _descriptors)
		{
			if (descriptor.MarkerFiles.Contains(fileName))
				return true;

			if (!string.IsNullOrWhiteSpace(extension) &&
			    descriptor.MarkerExtensions.Contains(extension))
			{
				return true;
			}
		}

		return false;
	}

	private bool HasKnownProjectMarkerByTargetedProbe(string rootPath)
	{
		foreach (var descriptor in _descriptors)
		{
			foreach (var markerFile in descriptor.MarkerFiles)
			{
				try
				{
					if (File.Exists(Path.Combine(rootPath, markerFile)))
						return true;
				}
				catch
				{
					// Marker probing is best-effort and must never break loading.
				}
			}
		}

		return false;
	}

	private static IReadOnlySet<string> FreezeOrEmpty(
		HashSet<string> values,
		IReadOnlySet<string> emptySet) =>
		values.Count == 0
			? emptySet
			: values.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
