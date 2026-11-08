namespace DevProjex.Mcp;

internal sealed partial class DevProjexMcpTools
{
	private const string SearchPathPatternNotice =
		"[Search] the pattern looks like a path, but search_project matches file content; find files by name with get_tree include_patterns.";

	internal const string MultilineSearchNotice =
		"[Search] multi-line pattern: a match may span lines and counts once at its first line.";

	// Lines of one multi-line match shown before the rest are summarised in a single line.
	internal const int MaximumShownSpanLines = 20;

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
