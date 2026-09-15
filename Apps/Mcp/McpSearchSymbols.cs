namespace DevProjex.Mcp;

/// <summary>
/// Names the declaration each search hit sits inside, so a reader can go from a hit to the thing
/// that contains it without guessing a line range and reading twice.
/// </summary>
/// <remarks>
/// Declarations come from the transformed snapshots that actually produced hits: one bounded parse
/// per such file, never one per hit, and never over the whole selection. A
/// declaration name is project text, so it is written inside the untrusted block together with the
/// match lines it describes; only the counts leave that block.
/// </remarks>
internal static class McpSearchSymbols
{
	/// <summary>
	/// A search can touch many files, and every one of them would be a parse. Hits beyond this many
	/// distinct files are left unannotated and counted, rather than turning a search into an index.
	/// </summary>
	public const int MaximumAnnotatedFiles = 64;

	public static McpSearchSymbolResult Resolve(
		IReadOnlyList<McpSearchHit> hits,
		IReadOnlyDictionary<string, IReadOnlyList<NavigationDeclaration>> navigationByFile,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(hits);
		ArgumentNullException.ThrowIfNull(navigationByFile);
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

		var spansByFile = new Dictionary<string, List<DeclarationSpan>>(StringComparer.Ordinal);
		foreach (var file in files)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!navigationByFile.TryGetValue(file.RelativePath, out var fileDeclarations))
				continue;

			var spans = fileDeclarations
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
			{
				declarations.Add(new McpSearchDeclaration(
					hit.RelativePath,
					best.Name,
					best.Start,
					best.End));
			}
			annotated++;
		}

		return new McpSearchSymbolResult(
			names,
			declarations,
			annotated,
			unannotatedFiles.Count,
			skippedFiles);
	}

	public static IReadOnlyList<NavigationDeclaration> CaptureNavigation(
		DependencyFactsEngine engine,
		string relativePath,
		string transformedText,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(engine);
		ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
		ArgumentNullException.ThrowIfNull(transformedText);
		var fingerprint = ContentFingerprint.Compute(transformedText.AsSpan()).ToHexString().ToLowerInvariant();
		return engine.ExtractNavigation(relativePath, transformedText, fingerprint, cancellationToken);
	}

	public static McpSearchDeclarationPreview? SelectDeclarationBodyPreview(
		IReadOnlyList<McpSearchCandidate> candidates,
		IReadOnlyList<McpSearchDeclaration> declarations,
		McpSearchRegex regex)
	{
		ArgumentNullException.ThrowIfNull(candidates);
		ArgumentNullException.ThrowIfNull(declarations);
		ArgumentNullException.ThrowIfNull(regex);
		if (declarations.Count == 0)
			return null;

		if (!regex.HasDeclarationBodyLiteralTerms)
			return FindPreview(candidates, declarations[0]);

		var declarationSet = declarations.ToHashSet();
		var previews = new Dictionary<McpSearchDeclaration, McpSearchDeclarationPreview>();
		var matchedLines = new Dictionary<McpSearchDeclaration, int>();
		var seenLines = new HashSet<DeclarationMatchLine>();
		foreach (var candidate in candidates)
		{
			var preview = candidate.DeclarationPreview;
			if (preview is null || !declarationSet.Contains(preview.Declaration))
				continue;

			previews.TryAdd(preview.Declaration, preview);
			var line = new DeclarationMatchLine(preview.Declaration, candidate.MatchLine);
			if (seenLines.Add(line))
				matchedLines[preview.Declaration] = matchedLines.GetValueOrDefault(preview.Declaration) + 1;
		}

		McpSearchDeclarationPreview? best = null;
		var bestNameQuality = McpDeclarationBodyNameMatchQuality.None;
		var bestMatchedLines = -1;
		foreach (var declaration in declarations)
		{
			if (!previews.TryGetValue(declaration, out var preview))
				continue;

			var nameQuality = regex.DeclarationBodyNameMatchQuality(declaration.Name);
			var lineCount = matchedLines.GetValueOrDefault(declaration);
			if (best is not null &&
				(nameQuality < bestNameQuality || nameQuality == bestNameQuality && lineCount <= bestMatchedLines))
			{
				continue;
			}

			best = preview;
			bestNameQuality = nameQuality;
			bestMatchedLines = lineCount;
		}

		return best;
	}

	private static McpSearchDeclarationPreview? FindPreview(
		IReadOnlyList<McpSearchCandidate> candidates,
		McpSearchDeclaration declaration) =>
		candidates
			.Select(static candidate => candidate.DeclarationPreview)
			.FirstOrDefault(preview => preview?.Declaration == declaration);

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
	public static McpSymbolLookup ResolveSymbol(
		DependencyFactsEngine engine,
		string relativePath,
		string transformedText,
		string symbol,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(engine);
		ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
		ArgumentNullException.ThrowIfNull(transformedText);
		var spans = CaptureNavigation(engine, relativePath, transformedText, cancellationToken)
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

	private readonly record struct DeclarationMatchLine(McpSearchDeclaration Declaration, int Line);
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
/// to ask for, and its inclusive line range.
/// </summary>
internal readonly record struct McpSearchDeclaration(
	string RelativePath,
	string Name,
	int StartLine,
	int EndLine);

internal sealed record McpSearchDeclarationPreview(
	McpSearchDeclaration Declaration,
	string Text,
	int RemainingLines,
	bool IsAddressable);

internal sealed class McpSearchDeclarationPreviewCache
{
	private readonly string relativePath;
	private readonly string content;
	private readonly IReadOnlyDictionary<string, int> declarationNameCounts;
	private readonly int maximumCharacters;
	private readonly CancellationToken cancellationToken;
	private readonly Dictionary<DeclarationIdentity, McpSearchDeclarationPreview> previews = [];
	private int[]? lineStarts;

	public McpSearchDeclarationPreviewCache(
		string relativePath,
		string content,
		IReadOnlyList<NavigationDeclaration> declarations,
		int maximumCharacters,
		CancellationToken cancellationToken)
	{
		this.relativePath = relativePath;
		this.content = content;
		declarationNameCounts = CountDeclarationNames(declarations);
		this.maximumCharacters = maximumCharacters;
		this.cancellationToken = cancellationToken;
	}

	public McpSearchDeclarationPreview Get(NavigationDeclaration declaration)
	{
		var identity = new DeclarationIdentity(declaration.Name, declaration.StartLine, declaration.EndLine);
		if (previews.TryGetValue(identity, out var preview))
			return preview;

		lineStarts ??= BuildLineStarts(content, cancellationToken);
		var body = Slice(declaration.StartLine, declaration.EndLine);
		preview = new McpSearchDeclarationPreview(
			new McpSearchDeclaration(
				relativePath,
				declaration.Name,
				declaration.StartLine,
				declaration.EndLine),
			body.Text,
			body.RemainingLines,
			declarationNameCounts[declaration.Name] == 1);
		previews[identity] = preview;
		return preview;
	}

	private (string Text, int RemainingLines) Slice(int startLine, int endLine)
	{
		var starts = lineStarts!;
		if (startLine < 1 || endLine < startLine || endLine > starts.Length)
			return (string.Empty, Math.Max(0, endLine - startLine + 1));

		var output = new StringBuilder(Math.Min(maximumCharacters, 1_024));
		var hasLine = false;
		for (var line = startLine; line <= endLine; line++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var start = starts[line - 1];
			var end = line < starts.Length ? starts[line] : content.Length;
			if (end > start && content[end - 1] == '\n')
				end--;
			if (end > start && content[end - 1] == '\r')
				end--;
			var lineText = content.AsSpan(start, end - start);
			var separator = hasLine ? 1 : 0;
			if ((long)output.Length + separator + lineText.Length > maximumCharacters)
			{
				if (!hasLine)
					AppendPrefix(output, lineText, maximumCharacters);
				return (output.ToString(), endLine - line + 1);
			}

			if (hasLine)
				output.Append('\n');
			output.Append(lineText);
			hasLine = true;
		}

		return (output.ToString(), 0);
	}

	private static int[] BuildLineStarts(string content, CancellationToken cancellationToken)
	{
		var starts = new List<int> { 0 };
		for (var index = 0; index < content.Length; index++)
		{
			if ((index & 0xFFF) == 0)
				cancellationToken.ThrowIfCancellationRequested();
			if (content[index] == '\r')
			{
				if (index + 1 < content.Length && content[index + 1] == '\n')
					index++;
				starts.Add(index + 1);
			}
			else if (content[index] == '\n')
				starts.Add(index + 1);
		}
		return starts.ToArray();
	}

	private static IReadOnlyDictionary<string, int> CountDeclarationNames(
		IReadOnlyList<NavigationDeclaration> declarations)
	{
		var counts = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (var declaration in declarations)
			counts[declaration.Name] = counts.GetValueOrDefault(declaration.Name) + 1;
		return counts;
	}

	private static void AppendPrefix(StringBuilder output, ReadOnlySpan<char> text, int maximumCharacters)
	{
		var length = Math.Min(text.Length, maximumCharacters);
		if (length > 0 && length < text.Length &&
			char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length]))
		{
			length--;
		}
		output.Append(text[..length]);
	}

	private readonly record struct DeclarationIdentity(string Name, int StartLine, int EndLine);
}

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
