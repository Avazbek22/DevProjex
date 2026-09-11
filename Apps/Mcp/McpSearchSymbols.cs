using System.Globalization;

namespace DevProjex.Mcp;

/// <summary>
/// Names the declaration each search hit sits inside, so a reader can go from a hit to the thing
/// that contains it without guessing a line range and reading twice.
/// </summary>
/// <remarks>
/// Declarations come from the dependency index built over the files that actually produced hits:
/// one bounded parse per such file, never one per hit, and never over the whole selection. A
/// declaration name is project text, so it is written inside the untrusted block together with the
/// match lines it describes; only the counts leave that block.
///
/// Hit lines are lines of the transformed text the tool returns, and the index parses the file on
/// disk. Redaction replaces a secret with a placeholder on the same line and adds no lines, so the
/// two agree on line numbers, which is the only coordinate this needs.
/// </remarks>
internal static class McpSearchSymbols
{
	public const string SectionHeading = "Enclosing declarations:";

	/// <summary>
	/// A search can touch many files, and every one of them would be a parse. Hits beyond this many
	/// distinct files are left unannotated and counted, rather than turning a search into an index.
	/// </summary>
	public const int MaximumAnnotatedFiles = 64;

	public static async Task<McpSearchSymbolResult> ResolveAsync(
		DependencyFactsEngine engine,
		ProjectContextPlan plan,
		IReadOnlyList<McpSearchHit> hits,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(engine);
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentNullException.ThrowIfNull(hits);
		if (hits.Count == 0)
			return McpSearchSymbolResult.None;

		var files = new List<McpSearchHit>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var skippedFiles = 0;
		foreach (var hit in hits)
		{
			if (!seen.Add(hit.RelativePath))
				continue;
			if (files.Count >= MaximumAnnotatedFiles)
			{
				skippedFiles++;
				continue;
			}

			files.Add(hit);
		}

		var index = await engine.IndexAsync(
				plan.SourceRoot,
				files.Select(static file => file.FullPath).ToArray(),
				progress: null,
				cancellationToken)
			.ConfigureAwait(false);

		var spansByFile = new Dictionary<string, List<DeclarationSpan>>(StringComparer.Ordinal);
		foreach (var file in files)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!index.FileByPath.TryGetValue(file.RelativePath, out var facts))
				continue;

			var spans = new List<DeclarationSpan>();
			foreach (var declaration in facts.Declarations)
			{
				foreach (var site in declaration.DeclarationSites)
				{
					// A site without an end line comes from an extractor that reports none, and a
					// one-line guess would claim containment it cannot know.
					if (site.EndLine >= site.Line &&
					    string.Equals(site.File, file.RelativePath, StringComparison.Ordinal))
					{
						spans.Add(new DeclarationSpan(site.Line, site.EndLine, declaration.Identity.QualifiedName));
					}
				}
			}

			if (spans.Count > 0)
				spansByFile[file.RelativePath] = spans;
		}

		var lines = new List<string>();
		var annotated = 0;
		var unannotatedFiles = new HashSet<string>(StringComparer.Ordinal);
		foreach (var hit in hits)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!spansByFile.TryGetValue(hit.RelativePath, out var spans))
			{
				unannotatedFiles.Add(hit.RelativePath);
				continue;
			}

			// The innermost declaration is the narrowest one that still contains the line.
			DeclarationSpan? best = null;
			foreach (var span in spans)
			{
				if (hit.Line < span.Start || hit.Line > span.End)
					continue;
				if (best is null || span.End - span.Start < best.End - best.Start)
					best = span;
			}

			if (best is null || string.IsNullOrEmpty(best.Name))
			{
				unannotatedFiles.Add(hit.RelativePath);
				continue;
			}

			lines.Add($"{hit.RelativePath}:{hit.Line.ToString(CultureInfo.InvariantCulture)}: {best.Name}");
			annotated++;
		}

		return new McpSearchSymbolResult(
			lines,
			annotated,
			unannotatedFiles.Count,
			skippedFiles);
	}

	private sealed record DeclarationSpan(int Start, int End, string Name);
}

internal readonly record struct McpSearchHit(string RelativePath, string FullPath, int Line);

internal sealed record McpSearchSymbolResult(
	IReadOnlyList<string> Lines,
	int AnnotatedHits,
	int FilesWithoutDeclarations,
	int FilesBeyondTheLimit)
{
	public static readonly McpSearchSymbolResult None = new([], 0, 0, 0);
}
