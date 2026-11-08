namespace DevProjex.Mcp;

internal sealed partial class DevProjexMcpTools
{
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

	internal static bool AppendSearchBatchRead(
		StringBuilder output,
		IReadOnlyList<McpSearchHit> hits,
		McpSearchSymbolResult symbols,
		IReadOnlyList<McpSearchRenderedGroup> groups,
		int contextLines,
		int maximumCharacters,
		McpDeclarationReadContext? readContext = null,
		IReadOnlyList<string>? exclusions = null)
	{
		var files = hits.GroupBy(static hit => hit.RelativePath, StringComparer.Ordinal).Take(5).ToArray();
		if (files.Length < 2)
			return false;

		var requests = new List<object>(files.Length);
		var remainingRanges = McpGetFileRequestSet.MaximumRanges;
		for (var index = 0; index < files.Length; index++)
		{
			var file = files[index];
			var ranges = new List<(int StartLine, int EndLine)>();
			foreach (var hit in file)
			{
				var name = symbols.Names.GetValueOrDefault(new McpSearchHitKey(file.Key, hit.Line));
				var declaration = symbols.Declarations.FirstOrDefault(item =>
					item.RelativePath == file.Key && item.Name == name);
				var range = declaration.StartLine > 0
					? symbols.DeclarationRanges.Of(declaration).FirstOrDefault(item =>
						hit.Line >= item.StartLine && hit.Line <= item.EndLine)
					: default;
				if (range.StartLine == 0)
				{
					var group = groups.First(item => item.RelativePath == file.Key && item.MatchLines.Contains(hit.Line));
					range = (Math.Max(group.Lines[0].LineNumber, hit.Line - contextLines),
						Math.Min(group.Lines[^1].LineNumber, hit.Line + contextLines));
				}
				ranges.Add(range);
			}

			var merged = new List<(int StartLine, int EndLine)>();
			foreach (var range in ranges.OrderBy(static item => item.StartLine).ThenBy(static item => item.EndLine))
			{
				if (merged.Count > 0 && (long)range.StartLine <= (long)merged[^1].EndLine + 1)
					merged[^1] = (merged[^1].StartLine, Math.Max(merged[^1].EndLine, range.EndLine));
				else
					merged.Add(range);
			}
			// Reserve one selection for every later file within get_file's shared batch limit.
			var selected = merged.Take(Math.Min(4, remainingRanges - (files.Length - index - 1))).ToArray();
			remainingRanges -= selected.Length;
			requests.Add(new
			{
				path = file.Key,
				ranges = selected.Select(static range => new { start_line = range.StartLine, end_line = range.EndLine })
			});
		}
		var arguments = new Dictionary<string, object?> { ["requests"] = requests };
		if (readContext is not null)
		{
			arguments["project"] = readContext.Project;
			if (readContext.Branch is not null)
				arguments["branch"] = readContext.Branch;
		}
		if (exclusions is not null)
			arguments["exclusions"] = exclusions;
		var line = "Read these in one call: get_file " +
			JsonSerializer.Serialize(arguments, McpToolResults.CompactJsonOptions);
		var separator = output.Length > 0 && output[^1] != '\n' ? Environment.NewLine : string.Empty;
		if ((long)output.Length + separator.Length + line.Length + Environment.NewLine.Length > maximumCharacters)
			return false;
		output.Append(separator).AppendLine(line);
		return true;
	}
}
