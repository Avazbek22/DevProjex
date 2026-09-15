using DevProjex.Application.Dependencies;
using TreeSitter;

namespace DevProjex.Infrastructure.Dependencies;

public sealed partial class TreeSitterDependencyFactExtractor
{
	private Tree ParseWithConditionalProjection(Parser parser, LanguageId language, string source,
		string relativePath, CancellationToken cancellationToken, out IReadOnlyList<SyntaxDamageRegion> omittedRegions)
	{
		var prepared = PrepareParseSource(language, source);
		omittedRegions = prepared.OmittedRegions;
		var original = ParseTree(parser, prepared.Source);
		Tree? projected = null;
		try
		{
			if (language != LanguageId.CSharp || !original.RootNode.HasError ||
				prepared.Source.IndexOf("#if", StringComparison.Ordinal) < 0)
				return original;
			var damage = AnalyzeSyntaxDamage(original.RootNode, relativePath, cancellationToken);
			if (damage is null)
				return original;
			var projection = PrepareDamagedConditionalRegions(prepared.Source, original.RootNode, damage, cancellationToken);
			if (projection.OmittedRegions.Count == 0)
				return original;
			projected = ParseTree(parser, projection.Source);
			var remaining = AnalyzeSyntaxDamage(projected.RootNode, relativePath, cancellationToken);
			if (remaining is not null && !HasLessSyntaxDamage(damage.Spans, remaining.Spans))
				return original;
			omittedRegions = prepared.OmittedRegions.Concat(projection.OmittedRegions).ToArray();
			original.Dispose();
			original = projected;
			projected = null;
			return original;
		}
		catch
		{
			original.Dispose();
			throw;
		}
		finally
		{
			projected?.Dispose();
		}
	}

	private Tree ParseTree(Parser parser, string source)
	{
		var tree = parser.Parse(source) ?? throw new InvalidOperationException("Tree-sitter returned no syntax tree.");
		Interlocked.Increment(ref _parseCount);
		return tree;
	}

	private static ParseSourcePreparation PrepareDamagedConditionalRegions(string source, Node root,
		SyntaxDamageAnalysis damage, CancellationToken cancellationToken)
	{
		var protectedSpans = ReadProtectedConditionalSpans(root, cancellationToken, out var bindingSpans);
		var lines = ReadSourceLines(source);
		char[]? projected = null;
		var omitted = new List<SyntaxDamageRegion>();
		for (var index = 0; index < lines.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!IsUnprotectedDirective(source, lines[index], "#if", protectedSpans))
				continue;
			var depth = 1;
			var endLine = -1;
			for (var candidate = index + 1; candidate < lines.Count; candidate++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (IsUnprotectedDirective(source, lines[candidate], "#if", protectedSpans))
					depth++;
				else if (IsUnprotectedDirective(source, lines[candidate], "#endif", protectedSpans) && --depth == 0)
				{
					endLine = candidate;
					break;
				}
			}
			if (endLine < 0)
				break;
			var start = lines[index].StartIndex;
			var end = lines[endLine].EndIndex;
			if (damage.Spans.Any(span => start < span.EndIndex && end > span.StartIndex) &&
				!bindingSpans.Any(span => start < span.EndIndex && end > span.StartIndex))
			{
				projected ??= source.ToCharArray();
				for (var offset = start; offset < end; offset++)
					if (projected[offset] is not ('\r' or '\n')) projected[offset] = ' ';
				omitted.Add(new SyntaxDamageRegion(start, end, index + 1, endLine + 1));
			}
			index = endLine;
		}
		return projected is null ? new ParseSourcePreparation(source, []) : new ParseSourcePreparation(new string(projected), omitted);
	}

	private static IReadOnlyList<SyntaxDamageSpan> ReadProtectedConditionalSpans(Node root, CancellationToken cancellationToken,
		out IReadOnlyList<SyntaxDamageSpan> bindingSpans)
	{
		var spans = new List<SyntaxDamageSpan>();
		var bindings = new List<SyntaxDamageSpan>();
		var pending = new Stack<Node>();
		pending.Push(root);
		var visited = 0;
		while (pending.TryPop(out var node))
		{
			if ((visited++ & 255) == 0)
				cancellationToken.ThrowIfCancellationRequested();
			// Removing a binder would reinterpret its surviving uses as ordinary project types.
			if (node.Type == "type_parameter_list")
				bindings.Add(new SyntaxDamageSpan(checked((int)node.StartIndex), checked((int)node.EndIndex)));
			if (node.Type is "comment" or "string_literal" or "verbatim_string_literal" or "raw_string_literal" or
				"interpolated_string_expression" or "character_literal")
			{
				spans.Add(new SyntaxDamageSpan(checked((int)node.StartIndex), checked((int)node.EndIndex)));
				continue;
			}
			foreach (var child in node.NamedChildren)
				pending.Push(child);
		}
		bindingSpans = bindings;
		return spans;
	}

	private static bool IsUnprotectedDirective(string source, SourceLine line, string directive,
		IReadOnlyList<SyntaxDamageSpan> protectedSpans)
	{
		if (!IsDirective(source, line, directive))
			return false;
		var marker = line.StartIndex;
		while (marker < line.EndIndex && char.IsWhiteSpace(source[marker])) marker++;
		return !protectedSpans.Any(span => marker >= span.StartIndex && marker < span.EndIndex);
	}

	// A retry may reduce damaged ownership, never move an error into previously proven source.
	private static bool HasLessSyntaxDamage(IReadOnlyList<SyntaxDamageSpan> original, IReadOnlyList<SyntaxDamageSpan> projected)
	{
		var before = MergeSyntaxDamageSpans(original);
		var after = MergeSyntaxDamageSpans(projected);
		return after.All(span => before.Any(owner => span.StartIndex >= owner.StartIndex && span.EndIndex <= owner.EndIndex)) &&
			after.Sum(span => Math.Max(1L, (long)span.EndIndex - span.StartIndex)) <
			before.Sum(span => Math.Max(1L, (long)span.EndIndex - span.StartIndex));
	}

	private static IReadOnlyList<SyntaxDamageSpan> MergeSyntaxDamageSpans(IReadOnlyList<SyntaxDamageSpan> spans)
	{
		var merged = new List<SyntaxDamageSpan>();
		foreach (var span in spans.OrderBy(span => span.StartIndex).ThenBy(span => span.EndIndex))
		{
			if (merged.Count > 0 && span.StartIndex <= merged[^1].EndIndex)
				merged[^1] = new SyntaxDamageSpan(merged[^1].StartIndex, Math.Max(merged[^1].EndIndex, span.EndIndex));
			else
				merged.Add(span);
		}
		return merged;
	}
}
