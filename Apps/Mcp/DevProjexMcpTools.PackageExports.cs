using DevProjex.Application.Dependencies;

namespace DevProjex.Mcp;

internal sealed partial class DevProjexMcpTools
{
	private async Task<IReadOnlyList<PackageExport>> FindPackageExportsAsync(
		ProjectContextPlan plan, CancellationToken cancellationToken)
	{
		var candidates = plan.IncludedFiles.Where(path => Path.GetFileName(path) is
			"package.json" or "jsr.json" or "deno.json" or "tsconfig.json").ToArray();
		if (candidates.Length == 0)
			return [];
		var documents = new Dictionary<string, string>(StringComparer.Ordinal);
		await using var inspected = await Projects.ConsumeSearchTextAsync(plan with { IncludedFiles = candidates },
			(file, _) =>
			{
				if (file.ReplacementRanges.Count == 0)
					documents[McpProjectService.ToRelative(plan.SourceRoot, file.Path)] = file.Content;
				return ValueTask.CompletedTask;
			}, cancellationToken).ConfigureAwait(false);
		var available = plan.IncludedFiles.ToDictionary(path => McpProjectService.ToRelative(plan.SourceRoot, path),
			StringComparer.Ordinal);
		var packageDirectories = documents.Keys.Where(static path => Path.GetFileName(path) is "package.json" or "jsr.json" or "deno.json")
			.Select(static path => Path.GetDirectoryName(path) ?? "").ToHashSet(StringComparer.Ordinal);
		var roots = documents.Keys.Where(path => Path.GetFileName(path) == "tsconfig.json" &&
			packageDirectories.Contains(Path.GetDirectoryName(path) ?? "")).ToHashSet(StringComparer.Ordinal);
		var availableConfigurations = available.Keys.Where(static path => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToArray();
		var pending = roots.Select(static path => (Path: path, InheritanceDepth: 0)).ToArray();
		var attempted = candidates.Select(path => McpProjectService.ToRelative(plan.SourceRoot, path)).ToHashSet(StringComparer.Ordinal);
		var expanded = new Dictionary<string, int>(StringComparer.Ordinal);
		while (pending.Length > 0)
		{
			var requested = new List<(string Path, int InheritanceDepth)>();
			foreach (var current in pending)
			{
				if (expanded.TryGetValue(current.Path, out var previousDepth) && previousDepth <= current.InheritanceDepth)
					continue;
				expanded[current.Path] = current.InheritanceDepth;
				if (current.InheritanceDepth < 4)
					requested.AddRange(PackageExportMap.ConfigurationDependencies(current.Path, documents[current.Path],
						references: false, projectRoot: plan.SourceRoot).Select(path => (path, current.InheritanceDepth + 1)));
				if (current.InheritanceDepth == 0 && roots.Contains(current.Path))
					requested.AddRange(PackageExportMap.ConfigurationDependencies(current.Path, documents[current.Path],
						references: true, projectRoot: plan.SourceRoot, includeInheritance: false).Select(static path => (path, 0)));
			}
			var next = requested.Select(item =>
				ProjectTreePathIdentity.TryResolveAvailableName(availableConfigurations, item.Path, out var resolved)
					? (Path: resolved, item.InheritanceDepth) : (Path: (string?)null, item.InheritanceDepth))
				.Where(static item => item.Path is not null)
				.GroupBy(static item => item.Path!, StringComparer.Ordinal)
				.Select(static group => (Path: group.Key, InheritanceDepth: group.Min(static item => item.InheritanceDepth)))
				.Where(item => !expanded.TryGetValue(item.Path, out var previousDepth) || previousDepth > item.InheritanceDepth)
				.OrderBy(static item => item.Path, StringComparer.Ordinal).ToArray();
			// Sixteen referenced candidates and four inheritance links bound new reads to 84 per package root.
			var missing = next.Where(item => attempted.Add(item.Path)).Select(static item => item.Path).ToArray();
			if (missing.Length > 0)
			{
				await using var configurations = await Projects.ConsumeSearchTextAsync(plan with { IncludedFiles = missing.Select(path => available[path]).ToArray() },
					(file, _) =>
					{
						if (file.ReplacementRanges.Count == 0)
							documents[McpProjectService.ToRelative(plan.SourceRoot, file.Path)] = file.Content;
						return ValueTask.CompletedTask;
					}, cancellationToken).ConfigureAwait(false);
			}
			pending = next.Where(item => documents.ContainsKey(item.Path)).ToArray();
		}

		var exports = PackageExportMap.Find(documents, plan.IncludedFiles
			.Select(path => McpProjectService.ToRelative(plan.SourceRoot, path))
			.Where(path => McpToolResults.ProtectMetadataString(path, McpProjectService.IsPrivateDataHidden(plan)) == path)
			.ToHashSet(StringComparer.Ordinal), plan.SourceRoot);
		return exports.Where(item =>
		{
			var metadata = string.Join('\n', item.Manifest, item.Key, item.Target);
			return Projects.RedactSyntheticText(plan, Path.Combine(plan.SourceRoot, item.Target), metadata,
				cancellationToken).Text == metadata;
		}).ToArray();
	}

	private static string? FormatPackageExports(IReadOnlyList<PackageExport> exports, string relativePath)
	{
		var matches = exports.Where(item => item.Target == relativePath).ToArray();
		if (matches.Length == 0)
			return null;
		var retained = new List<PackageExport>();
		var characters = 0;
		foreach (var item in matches)
		{
			var length = item.Manifest.Length + JsonSerializer.Serialize(item.Key).Length + 4;
			if (retained.Count == 8 || characters + length > 1200)
				break;
			retained.Add(item);
			characters += length;
		}
		if (retained.Count == 0)
			return null;
		var shown = retained.GroupBy(item => item.Manifest, StringComparer.Ordinal)
			.Select(group => McpTextEscaping.EscapeSingleLine(group.Key) + " " + string.Join(", ", group.Select(item =>
				JsonSerializer.Serialize(item.Key, McpToolResults.CompactJsonOptions))));
		return "Exported by: " + string.Join(", ", shown) + (matches.Length > retained.Count ? $" (+{matches.Length - retained.Count} more)" : "");
	}
}
