namespace DevProjex.Mcp;

internal sealed partial class DevProjexMcpTools
{
	private const string SearchPathPatternNotice =
		"[Search] the pattern looks like a path, but search_project matches file content; find files by name with get_tree include_patterns.";

	internal static string? FormatPerLineSearchNotice(string pattern, int totalMatches) =>
		totalMatches == 0 && (pattern.Contains("\\n", StringComparison.Ordinal) || pattern.Contains('\n'))
			? "[Search] matching is per line, so a pattern with \\n cannot match; search for each line separately."
			: null;

	internal static McpSearchRenderSlice RenderSearchGroupsWithDocumentationLast(
		IReadOnlyList<McpSearchRenderedGroup> ordered,
		int maximumResults,
		int maximumCharacters,
		bool includeHeading = true)
	{
		var code = RenderSearchGroups(ordered.Where(static group => !McpSearchSymbols.IsDocumentationPath(group.RelativePath)).ToArray(),
			maximumResults, maximumCharacters);
		var heading = includeHeading ? "Matches in documentation files:" + Environment.NewLine : string.Empty;
		var documentation = RenderSearchGroups(ordered.Where(static group => McpSearchSymbols.IsDocumentationPath(group.RelativePath)).ToArray(),
			maximumResults - code.ShownMatches, Math.Max(0, maximumCharacters - code.Output.Length - heading.Length));
		if (documentation.ShownMatches == 0)
			return code with { Truncated = code.Truncated || documentation.Truncated };
		var offset = code.Output.Length + heading.Length;
		code.Output.Append(heading).Append(documentation.Output);
		code.WrittenHits.AddRange(documentation.WrittenHits);
		code.RenderedLines.AddRange(documentation.RenderedLines.Select(line => line with { Offset = line.Offset + offset }));
		return code with
		{
			ShownMatches = code.ShownMatches + documentation.ShownMatches,
			Truncated = code.Truncated || documentation.Truncated
		};
	}
}
