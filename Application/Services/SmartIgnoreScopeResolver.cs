using System.Collections.Frozen;

namespace DevProjex.Application.Services;

public sealed class SmartIgnoreScopeResolver : ISmartIgnoreScopeResolver
{
	private readonly string _rootPath;
	private readonly ProjectRootFactsProvider _rootFactsProvider;
	private readonly SmartIgnoreRuleDescriptor[] _descriptors;
	private readonly FrozenDictionary<string, SmartIgnoreRuleDescriptor[]> _directoryRules;
	private readonly FrozenDictionary<string, SmartIgnoreRuleDescriptor[]> _fileRules;

	public SmartIgnoreScopeResolver(
		string rootPath,
		IReadOnlyList<SmartIgnoreRuleDescriptor> descriptors,
		ProjectRootFactsProvider rootFactsProvider)
		: this(
			rootPath,
			rootFactsProvider,
			descriptors.ToArray(),
			BuildRuleIndex(descriptors, static descriptor => descriptor.FolderNames),
			BuildRuleIndex(descriptors, static descriptor => descriptor.FileNames))
	{
	}

	internal SmartIgnoreScopeResolver(
		string rootPath,
		ProjectRootFactsProvider rootFactsProvider,
		SmartIgnoreRuleDescriptor[] descriptors,
		FrozenDictionary<string, SmartIgnoreRuleDescriptor[]> directoryRules,
		FrozenDictionary<string, SmartIgnoreRuleDescriptor[]> fileRules)
	{
		_rootPath = PathUtility.Normalize(rootPath);
		_rootFactsProvider = rootFactsProvider;
		_descriptors = descriptors;
		_directoryRules = directoryRules;
		_fileRules = fileRules;
	}

	public SmartIgnoreScopeDecision EvaluateDirectory(string fullPath, string name) =>
		Evaluate(
			fullPath,
			name,
			isDirectory: true,
			_directoryRules);

	public SmartIgnoreScopeDecision EvaluateFile(string fullPath, string name) =>
		Evaluate(
			fullPath,
			name,
			isDirectory: false,
			_fileRules);

	private SmartIgnoreScopeDecision Evaluate(
		string fullPath,
		string name,
		bool isDirectory,
		IReadOnlyDictionary<string, SmartIgnoreRuleDescriptor[]> ruleIndex)
	{
		if (!ruleIndex.TryGetValue(name, out var relevantRules))
			return SmartIgnoreScopeDecision.Unresolved;

		string normalizedPath;
		try
		{
			normalizedPath = PathUtility.Normalize(fullPath);
		}
		catch
		{
			return SmartIgnoreScopeDecision.Unresolved;
		}

		if (!IsWithinRoot(normalizedPath))
			return SmartIgnoreScopeDecision.Unresolved;

		// A nested project marker protects collision-prone candidates. Distinctive
		// artifact directories such as node_modules may contain package metadata of
		// their own, but that metadata does not turn the dependency store into a project.
		var candidateOwnsScope = isDirectory &&
		                         HasKnownProjectMarker(_rootFactsProvider.Get(normalizedPath));

		var currentDirectory = Path.GetDirectoryName(normalizedPath);
		var decision = SmartIgnoreScopeDecision.Unresolved;
		while (!string.IsNullOrWhiteSpace(currentDirectory) &&
		       IsWithinRoot(currentDirectory))
		{
			var rootFacts = _rootFactsProvider.Get(currentDirectory);
			var ownsNestedScope = HasKnownProjectMarker(rootFacts);
			var hasApplicableRule = false;
			var requiresEvidenceForEveryApplicableRule = true;
			foreach (var rule in relevantRules)
			{
				if (!rootFacts.HasAnyMarkerFile(rule.MarkerFiles) &&
				    !rootFacts.HasAnyFileExtension(rule.MarkerExtensions))
					continue;

				hasApplicableRule = true;
				if (!isDirectory || !rule.EvidenceRequiredFolderNames.Contains(name))
				{
					requiresEvidenceForEveryApplicableRule = false;
					break;
				}
			}

			if (hasApplicableRule)
			{
				var isIgnored = !requiresEvidenceForEveryApplicableRule ||
				                !candidateOwnsScope &&
				                SmartArtifactIgnoreMatcher.Default.IsIgnoredDirectory(fullPath, name);
				decision = isIgnored
					? SmartIgnoreScopeDecision.Exclude
					: SmartIgnoreScopeDecision.Include;
			}

			// The nearest marked project owns its descendants. Continuing into a parent
			// would let a frontend rule, for example, hide a same-named source directory
			// inside a nested .NET project.
			if (decision.IsResolved || ownsNestedScope)
			{
				if (!decision.IsResolved)
					decision = SmartIgnoreScopeDecision.Include;
				break;
			}

			if (PathComparer.Default.Equals(currentDirectory, _rootPath))
				break;

			currentDirectory = Path.GetDirectoryName(currentDirectory);
		}

		// Project markers can change between refreshes, so scope ownership is not cached.
		return decision;
	}

	private bool HasKnownProjectMarker(ProjectRootFacts rootFacts)
	{
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

	private bool IsWithinRoot(string path)
	{
		if (_rootPath.Length == 0)
			return false;
		if (string.Equals(path, _rootPath, PathUtility.DefaultComparison))
			return true;
		if (!path.StartsWith(_rootPath, PathUtility.DefaultComparison) ||
		    path.Length <= _rootPath.Length)
		{
			return false;
		}

		if (_rootPath[^1] == Path.DirectorySeparatorChar ||
		    _rootPath[^1] == Path.AltDirectorySeparatorChar)
		{
			return true;
		}

		var separator = path[_rootPath.Length];
		return separator == Path.DirectorySeparatorChar ||
		       separator == Path.AltDirectorySeparatorChar;
	}

	internal static FrozenDictionary<string, SmartIgnoreRuleDescriptor[]> BuildRuleIndex(
		IReadOnlyList<SmartIgnoreRuleDescriptor> descriptors,
		Func<SmartIgnoreRuleDescriptor, IReadOnlySet<string>> getNames)
	{
		var index = new Dictionary<string, List<SmartIgnoreRuleDescriptor>>(StringComparer.OrdinalIgnoreCase);
		foreach (var descriptor in descriptors)
		{
			if (descriptor.MarkerFiles.Count == 0 && descriptor.MarkerExtensions.Count == 0)
				continue;

			foreach (var name in getNames(descriptor))
			{
				if (!index.TryGetValue(name, out var rules))
				{
					rules = [];
					index.Add(name, rules);
				}

				rules.Add(descriptor);
			}
		}

		return index.ToFrozenDictionary(
			static pair => pair.Key,
			static pair => pair.Value.ToArray(),
			StringComparer.OrdinalIgnoreCase);
	}
}
