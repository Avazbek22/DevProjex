namespace DevProjex.Application.UseCases;

public static class ProjectTreeInventoryExtensionDiscovery
{
	public static HashSet<string> GetVisibleExtensions(
		ProjectTreeInventorySnapshot inventory,
		IgnoreRules rules,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (inventory.Entries.Count == 0)
			return extensions;

		ref readonly var root = ref inventory.GetEntryRef(0);
		var gitIgnoreContext = rules.CreateGitIgnoreScanContext(root.FullPath);
		var rootGitIgnore = rules.UseGitIgnore
			? gitIgnoreContext.Evaluate(root.FullPath, root.RelativePath, isDirectory: true, root.Name)
			: IgnoreRules.GitIgnoreEvaluation.NotIgnored;
		if (ShouldSkipDirectory(in root, rules, rootGitIgnore))
			return extensions;

		var pendingDirectories = new Stack<int>();
		pendingDirectories.Push(0);
		while (pendingDirectories.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var parentIndex = pendingDirectories.Pop();
			ref readonly var parent = ref inventory.GetEntryRef(parentIndex);
			var shouldApplySmartIgnore = rules.ShouldApplySmartIgnore(parent.FullPath, isDirectory: true);
			for (var childOffset = 0; childOffset < parent.ChildCount; childOffset++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var childIndex = parent.FirstChildIndex + childOffset;
				ref readonly var child = ref inventory.GetEntryRef(childIndex);
				var gitIgnore = rules.UseGitIgnore
					? gitIgnoreContext.Evaluate(
						child.FullPath,
						child.RelativePath,
						child.IsDirectory,
						child.Name)
					: IgnoreRules.GitIgnoreEvaluation.NotIgnored;

				if (child.IsDirectory)
				{
					if (!ShouldSkipDirectory(in child, rules, gitIgnore))
						pendingDirectories.Push(childIndex);
					continue;
				}

				if (IgnoreDecisionEngine.EvaluateFile(
					    child.FullPath,
					    child.Name,
					    child.IsHidden,
					    child.Length,
					    rules,
					    shouldApplySmartIgnore,
					    gitIgnore).IsIgnored)
				{
					continue;
				}

				AddExtension(child.Name, extensions);
			}
		}

		return extensions;
	}

	private static bool ShouldSkipDirectory(
		in ProjectTreeInventoryEntry entry,
		IgnoreRules rules,
		in IgnoreRules.GitIgnoreEvaluation gitIgnoreEvaluation) =>
		IgnoreDecisionEngine.EvaluateDirectory(
			entry.FullPath,
			entry.Name,
			entry.IsHidden,
			rules,
			gitIgnoreEvaluation).IsIgnored;

	private static void AddExtension(string fileName, HashSet<string> extensions)
	{
		if (IgnoreRuleSemantics.IsExtensionlessFileName(fileName))
		{
			extensions.Add(fileName);
			return;
		}

		var extension = Path.GetExtension(fileName.AsSpan());
		if (extension.IsEmpty)
			return;

		if (extensions.TryGetAlternateLookup<ReadOnlySpan<char>>(out var lookup) && lookup.Contains(extension))
			return;

		extensions.Add(extension.ToString());
	}
}
