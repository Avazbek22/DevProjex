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

			var spans = facts.NavigationDeclarations
				.Where(declaration => string.Equals(
					declaration.ContentFingerprint,
					facts.ContentFingerprint,
					StringComparison.Ordinal))
				.Select(static declaration => new DeclarationSpan(
					declaration.StartLine,
					declaration.EndLine,
					declaration.Name,
					declaration.EndIndex - declaration.StartIndex))
				.ToList();

			if (spans.Count > 0)
				spansByFile[file.RelativePath] = spans;
		}

		var names = new Dictionary<McpSearchHitKey, string>();
		var declarations = new List<McpSearchDeclaration>();
		var declared = new HashSet<string>(StringComparer.Ordinal);
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
				if (best is null || span.End - span.Start < best.End - best.Start ||
				    span.End - span.Start == best.End - best.Start && span.CharacterLength < best.CharacterLength)
					best = span;
			}

			if (best is null || string.IsNullOrEmpty(best.Name))
			{
				unannotatedFiles.Add(hit.RelativePath);
				continue;
			}

			names[new McpSearchHitKey(hit.RelativePath, hit.Line)] = best.Name;
			// One entry per declaration, not per hit: this is the list a caller reads back, and a
			// declaration touched by ten matches is still one thing to open.
			if (declared.Add($"{hit.RelativePath}\u0000{best.Name}"))
				declarations.Add(new McpSearchDeclaration(hit.RelativePath, best.Name, best.Start));
			annotated++;
		}

		return new McpSearchSymbolResult(
			names,
			declarations,
			annotated,
			unannotatedFiles.Count,
			skippedFiles);
	}

	/// <summary>
	/// Turns a declaration name into the lines that declare it, so a caller can read a symbol
	/// without first learning where it lives.
	/// </summary>
	/// <remarks>
	/// A fully qualified name wins outright. Otherwise the last segment of each declared name is
	/// compared, and a name that matches more than one declaration is reported as ambiguous rather
	/// than resolved to whichever came first: choosing silently would return the wrong code with
	/// nothing in the response to say so.
	/// </remarks>
	public static async Task<McpSymbolLookup> ResolveSymbolAsync(
		DependencyFactsEngine engine,
		ProjectContextPlan plan,
		string relativePath,
		string fullPath,
		string symbol,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(engine);
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

		var index = await engine
			.IndexAsync(plan.SourceRoot, [fullPath], progress: null, cancellationToken)
			.ConfigureAwait(false);
		if (!index.FileByPath.TryGetValue(relativePath, out var facts) ||
		    facts.Status != DependencyFileStatus.Supported)
		{
			return McpSymbolLookup.Unsupported;
		}

		var spans = facts.NavigationDeclarations
			.Where(declaration => string.Equals(
				declaration.ContentFingerprint,
				facts.ContentFingerprint,
				StringComparison.Ordinal))
			.Select(static declaration => new DeclarationSpan(
				declaration.StartLine,
				declaration.EndLine,
				declaration.Name,
				declaration.EndIndex - declaration.StartIndex))
			.ToList();

		if (spans.Count == 0)
			return McpSymbolLookup.Unsupported;

		var qualified = spans
			.Where(span => string.Equals(span.Name, symbol, StringComparison.Ordinal))
			.ToArray();
		if (qualified.Length == 1)
			return McpSymbolLookup.Found(qualified[0].Start, qualified[0].End);
		if (qualified.Length > 1)
			return McpSymbolLookup.Ambiguous(qualified.Length);

		var ownerQualified = spans
			.Where(span => span.Name.EndsWith('.' + symbol, StringComparison.Ordinal))
			.ToArray();
		if (ownerQualified.Length == 1)
			return McpSymbolLookup.Found(ownerQualified[0].Start, ownerQualified[0].End);
		if (ownerQualified.Length > 1)
			return McpSymbolLookup.Ambiguous(ownerQualified.Length);

		var simple = spans.Where(span => LastSegment(span.Name).Equals(symbol, StringComparison.Ordinal)).ToArray();
		return simple.Length switch
		{
			1 => McpSymbolLookup.Found(simple[0].Start, simple[0].End),
			> 1 => McpSymbolLookup.Ambiguous(simple.Length),
			_ => McpSymbolLookup.Unknown
		};
	}

	private static ReadOnlySpan<char> LastSegment(string name)
	{
		var separator = name.AsSpan().LastIndexOfAny('.', '#', '/');
		return separator >= 0 ? name.AsSpan(separator + 1) : name.AsSpan();
	}

	private sealed record DeclarationSpan(int Start, int End, string Name, int CharacterLength);
}

internal enum McpSymbolLookupStatus
{
	Resolved,
	Unknown,
	Ambiguous,
	Unsupported
}

internal readonly record struct McpSymbolLookup(
	McpSymbolLookupStatus Status,
	int StartLine,
	int EndLine,
	int CandidateCount)
{
	public static readonly McpSymbolLookup Unknown = new(McpSymbolLookupStatus.Unknown, 0, 0, 0);
	public static readonly McpSymbolLookup Unsupported = new(McpSymbolLookupStatus.Unsupported, 0, 0, 0);

	public static McpSymbolLookup Found(int startLine, int endLine) =>
		new(McpSymbolLookupStatus.Resolved, startLine, endLine, 1);

	public static McpSymbolLookup Ambiguous(int candidates) =>
		new(McpSymbolLookupStatus.Ambiguous, 0, 0, candidates);
}

internal readonly record struct McpSearchHit(string RelativePath, string FullPath, int Line);

/// <summary>One matched line, as the key the renderer and the naming agree on.</summary>
internal readonly record struct McpSearchHitKey(string RelativePath, int Line);

/// <summary>
/// One rendered line of one group, kept so that the slice a response shows can be chosen after the
/// whole result is known rather than as each file streams past.
/// </summary>
internal readonly record struct McpSearchGroupLine(int LineNumber, bool IsMatch, string Text);

/// <summary>
/// One context group, rendered and set aside. The path heading and the group separator are not in
/// <see cref="Lines"/>: both depend on what ends up next to the group, which is not known until the
/// slice is chosen.
/// </summary>
internal sealed record McpSearchRenderedGroup(
	string RelativePath,
	string FullPath,
	IReadOnlyList<int> MatchLines,
	IReadOnlyList<McpSearchGroupLine> Lines);

/// <summary>
/// One line the renderer wrote in full, and where it starts in the response. A declaration header
/// is placed at one of these offsets after the render finishes.
/// </summary>
internal readonly record struct McpSearchRenderedLine(
	string RelativePath,
	int Offset,
	int LineNumber,
	bool IsMatch,
	bool StartsGroup);

/// <summary>
/// One declaration a search touched, in the shape a caller passes back: the file to open, the name
/// to ask for, and the line it starts on.
/// </summary>
internal readonly record struct McpSearchDeclaration(string RelativePath, string Name, int Line);

internal sealed record McpSearchSymbolResult(
	IReadOnlyDictionary<McpSearchHitKey, string> Names,
	IReadOnlyList<McpSearchDeclaration> Declarations,
	int AnnotatedHits,
	int FilesWithoutDeclarations,
	int FilesBeyondTheLimit)
{
	public static readonly McpSearchSymbolResult None =
		new(new Dictionary<McpSearchHitKey, string>(), [], 0, 0, 0);
}
