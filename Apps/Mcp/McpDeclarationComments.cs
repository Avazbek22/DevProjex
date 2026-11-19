using DevProjex.Application.Dependencies;

namespace DevProjex.Mcp;

internal sealed class McpDeclarationComments
{
	private const int MaximumCommentLines = 200;
	private readonly CommentRange[] ranges;

	private McpDeclarationComments(CommentRange[] ranges) => this.ranges = ranges;

	public static McpDeclarationComments Capture(string relativePath, string content,
		IReadOnlyList<NavigationDeclaration> declarations, CancellationToken cancellationToken)
	{
		var lines = McpLargeFileOutlineBuilder.NormalizeLineEndings(content).Split('\n');
		var hash = Path.GetExtension(relativePath).ToLowerInvariant() is ".py" or ".pyi" or ".rb" or ".rake";
		var result = new List<CommentRange>();
		for (var index = 0; index < declarations.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var declaration = declarations[index];
			var end = declaration.StartLine - 2;
			if (end < 0 || end >= lines.Length) continue;
			var first = end + 1;
			var limit = Math.Max(0, end - MaximumCommentLines + 1);
			for (var line = end; line >= limit;)
			{
				var text = lines[line].AsSpan().Trim();
				if (text.StartsWith(hash ? "#" : "//", StringComparison.Ordinal))
				{
					first = line--;
					continue;
				}
				if (hash || !text.EndsWith("*/", StringComparison.Ordinal)) break;
				if (text.Contains("/*", StringComparison.Ordinal) && !text.StartsWith("/*", StringComparison.Ordinal)) break;
				var opening = line;
				while (opening >= limit && !lines[opening].AsSpan().TrimStart().StartsWith("/*", StringComparison.Ordinal))
				{
					if (lines[opening].AsSpan().Trim().IsEmpty) break;
					opening--;
				}
				if (opening < limit || !lines[opening].AsSpan().TrimStart().StartsWith("/*", StringComparison.Ordinal)) break;
				first = opening;
				line = opening - 1;
			}
			if (first <= end)
				result.Add(new(first + 1, end + 1, declaration, index));
		}
		return new(result.OrderBy(static range => range.Start).ThenBy(static range => range.Index).ToArray());
	}

	public NavigationDeclaration? Find(int line, NavigationDeclaration? original)
	{
		var low = 0;
		var high = ranges.Length;
		while (low < high)
		{
			var middle = low + (high - low) / 2;
			if (ranges[middle].Start <= line) low = middle + 1;
			else high = middle;
		}
		CommentRange? best = null;
		for (var index = low - 1; index >= 0 && line - ranges[index].Start < MaximumCommentLines; index--)
		{
			var range = ranges[index];
			if (line > range.End || original is not null &&
				(original.StartLine > range.Declaration.StartLine || original.EndLine < range.Declaration.EndLine))
				continue;
			if (best is null || Priority(range).CompareTo(Priority(best)) < 0)
				best = range;
		}
		return best?.Declaration ?? original;
	}

	private static (long Lines, long Characters, int Index) Priority(CommentRange range) =>
		((long)range.Declaration.EndLine - range.Declaration.StartLine,
			(long)range.Declaration.EndIndex - range.Declaration.StartIndex, range.Index);

	private sealed record CommentRange(int Start, int End, NavigationDeclaration Declaration, int Index);
}
