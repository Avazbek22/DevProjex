using System.Collections.Concurrent;
using System.Collections.Frozen;

namespace DevProjex.Application.Services;

public sealed class SmartIgnoreScopeResolver : ISmartIgnoreScopeResolver
{
	private const int ResultCacheLimit = 4_096;
	private readonly string _rootPath;
	private readonly ProjectRootFactsProvider _rootFactsProvider;
	private readonly FrozenDictionary<string, SmartIgnoreRuleDescriptor[]> _directoryRules;
	private readonly FrozenDictionary<string, SmartIgnoreRuleDescriptor[]> _fileRules;
	private readonly ConcurrentDictionary<string, bool> _directoryResults;
	private readonly ConcurrentDictionary<string, bool> _fileResults;

	public SmartIgnoreScopeResolver(
		string rootPath,
		IReadOnlyList<SmartIgnoreRuleDescriptor> descriptors,
		ProjectRootFactsProvider rootFactsProvider)
		: this(
			rootPath,
			rootFactsProvider,
			BuildRuleIndex(descriptors, static descriptor => descriptor.FolderNames),
			BuildRuleIndex(descriptors, static descriptor => descriptor.FileNames))
	{
	}

	internal SmartIgnoreScopeResolver(
		string rootPath,
		ProjectRootFactsProvider rootFactsProvider,
		FrozenDictionary<string, SmartIgnoreRuleDescriptor[]> directoryRules,
		FrozenDictionary<string, SmartIgnoreRuleDescriptor[]> fileRules)
	{
		_rootPath = PathUtility.Normalize(rootPath);
		_rootFactsProvider = rootFactsProvider;
		_directoryRules = directoryRules;
		_fileRules = fileRules;
		_directoryResults = new ConcurrentDictionary<string, bool>(PathComparer.Default);
		_fileResults = new ConcurrentDictionary<string, bool>(PathComparer.Default);
	}

	public bool IsIgnoredDirectory(string fullPath, string name) =>
		IsIgnored(fullPath, name, _directoryRules, _directoryResults);

	public bool IsIgnoredFile(string fullPath, string name) =>
		IsIgnored(fullPath, name, _fileRules, _fileResults);

	private bool IsIgnored(
		string fullPath,
		string name,
		IReadOnlyDictionary<string, SmartIgnoreRuleDescriptor[]> ruleIndex,
		ConcurrentDictionary<string, bool> resultCache)
	{
		if (!ruleIndex.TryGetValue(name, out var relevantRules))
			return false;

		string normalizedPath;
		try
		{
			normalizedPath = PathUtility.Normalize(fullPath);
		}
		catch
		{
			return false;
		}

		if (!IsWithinRoot(normalizedPath))
			return false;
		if (resultCache.TryGetValue(normalizedPath, out var cached))
			return cached;

		var currentDirectory = Path.GetDirectoryName(normalizedPath);
		var isIgnored = false;
		while (!string.IsNullOrWhiteSpace(currentDirectory) &&
		       IsWithinRoot(currentDirectory))
		{
			var rootFacts = _rootFactsProvider.Get(currentDirectory);
			foreach (var rule in relevantRules)
			{
				if (rootFacts.HasAnyMarkerFile(rule.MarkerFiles) ||
				    rootFacts.HasAnyFileExtension(rule.MarkerExtensions))
				{
					isIgnored = true;
					break;
				}
			}

			if (isIgnored || PathComparer.Default.Equals(currentDirectory, _rootPath))
				break;

			currentDirectory = Path.GetDirectoryName(currentDirectory);
		}

		resultCache[normalizedPath] = isIgnored;
		if (resultCache.Count > ResultCacheLimit)
			resultCache.Clear();

		return isIgnored;
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
