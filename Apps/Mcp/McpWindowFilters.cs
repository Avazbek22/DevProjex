namespace DevProjex.Mcp;

internal static class McpWindowFilters
{
	internal const int MaximumReadableFiles = 100_000;

	public static string[] Values(ProjectSelectionProfile? profile)
	{
		profile ??= new ProjectSelectionProfile([], [], []);
		var selection = ProjectSelectionAdapter.FromLegacyProfile(profile, ProjectProfileReference.Local);
		var values = new List<string>();
		AddSelection("extension", selection.Extensions, profile.ExtensionStates, StringComparer.OrdinalIgnoreCase);
		AddSelection("root folder", selection.Roots, profile.RootFolderStates, ProjectTreePathIdentity.CanonicalComparer);
		values.Add("git mode " + ProjectSelectionTokens.ToToken(selection.GitMode!.Value));
		values.AddRange((selection.Exclusions ?? []).Select(exclusion => "exclusion " + ProjectSelectionTokens.ToToken(exclusion)));
		return values.Order(StringComparer.Ordinal).ToArray();

		void AddSelection(string kind, IReadOnlyCollection<string>? items, IReadOnlyDictionary<string, bool>? states, StringComparer comparer)
		{
			if (items is null || items.Count == 0)
			{
				values.Add(kind + " selection " + (items is null ? "all" : "none"));
			}
			else
				values.AddRange(items.Distinct(comparer).Select(item => kind + " " + Name(item)));
			if (states is not null)
				values.AddRange(states.Where(pair => !pair.Value).Select(pair => "exclusion " + kind + " " + Name(pair.Key)));

			string Name(string item)
			{
				if (kind != "extension")
					return JsonSerializer.Serialize(item.Replace('\\', '/'));
				var extension = item.ToLowerInvariant();
				return extension.StartsWith('.') && !extension.Any(char.IsWhiteSpace) && !extension.Any(char.IsControl)
					? extension
					: JsonSerializer.Serialize(extension);
			}
		}
	}

	public static string Identity(ProjectSelectionProfile? profile) => JsonSerializer.Serialize(Values(profile));

	public static string[] Changes(ProjectSelectionProfile? before, ProjectSelectionProfile? after)
	{
		var previous = Values(before).ToHashSet(StringComparer.Ordinal);
		var current = Values(after).ToHashSet(StringComparer.Ordinal);
		return previous.Except(current).Order(StringComparer.Ordinal).Select(value => "-" + value)
			.Concat(current.Except(previous).Order(StringComparer.Ordinal).Select(value => "+" + value)).ToArray();
	}

	public static string FormatValues(IEnumerable<string> values)
	{
		var all = values.ToArray();
		var shown = all.Take(10).Select(value =>
		{
			var output = new StringBuilder();
			var complete = SingleLineTextEscaping.AppendBounded(output, value.AsSpan(), 512, preserveTabs: false);
			return output + (complete ? "" : "…");
		});
		return string.Join(Environment.NewLine, shown) + (all.Length > 10 ? $"{Environment.NewLine}· {all.Length - 10} more" : "");
	}

	public static string FormatCounts(IReadOnlySet<string>? before, IReadOnlySet<string>? after)
	{
		if (before is null || after is null)
			return "";
		var removed = before.Count(path => !after.Contains(path));
		var added = after.Count(path => !before.Contains(path));
		var parts = new List<string>();
		if (removed > 0) parts.Add($"{removed} files no longer readable");
		if (added > 0) parts.Add($"{added} now readable");
		return parts.Count == 0 ? "" : " (" + string.Join(", ", parts) + ")";
	}

	public static IReadOnlySet<string>? CaptureReadableFiles(ProjectContextPlan plan)
	{
		if (plan.HasErrors || plan.Analysis.Diagnostics.HadAccessDenied || plan.Analysis.Diagnostics.RootAccessDenied ||
			!plan.GitReadiness.IsReady || plan.GitReadiness.UnavailableTrackedIndexCount > 0)
			return null;
		var files = new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer);
		var pending = new Stack<TreeNodeDescriptor>();
		pending.Push(plan.EffectiveTree);
		while (pending.TryPop(out var node))
		{
			if (node.IsAccessDenied)
				return null;
			if (!node.IsDirectory)
			{
				files.Add(PathUtility.GetPortableRelativePath(plan.SourceRoot, node.FullPath));
				if (files.Count > MaximumReadableFiles)
					return null;
			}
			foreach (var child in node.Children) pending.Push(child);
		}
		return files;
	}
}
