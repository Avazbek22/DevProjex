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
		var exports = PackageExportMap.Find(documents, plan.IncludedFiles
			.Select(path => McpProjectService.ToRelative(plan.SourceRoot, path))
			.Where(path => McpToolResults.ProtectMetadataString(path, McpProjectService.IsPrivateDataHidden(plan)) == path)
			.ToHashSet(StringComparer.Ordinal));
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
