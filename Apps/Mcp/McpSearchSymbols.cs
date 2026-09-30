using System.Globalization;

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
	/// Limits final declaration annotations, not the navigation parses used to score search candidates.
	/// Additional matching files are counted as unannotated.
	/// </summary>
	public const int MaximumAnnotatedFiles = 64;

	private static readonly char[] DeclarationTailStarts = ['(', '<', '{', '[', ';', ',', '='];
	private static readonly char[] RegularExpressionOnlySyntax = ['\\', '^', '+'];
	private static readonly char[] NameMetacharacters = ['*', '?', '(', ')', '[', ']', '{', '}'];

	/// <summary>
	/// Reduces a declaration-name query to the names it asks for. Readers write the declaration
	/// they picture, such as "class Foo", "type Foo|interface Foo" or "def foo(", so each
	/// alternative keeps its last word, cut before any parameter list or type arguments.
	/// </summary>
	public static IReadOnlyList<string> ParseDeclarationNames(string pattern)
	{
		ArgumentNullException.ThrowIfNull(pattern);
		var names = new List<string>();
		foreach (var alternative in pattern.Split('|', StringSplitOptions.TrimEntries))
		{
			var name = FindDeclaredName(alternative);
			if (name.Length > 0 && !names.Contains(name, StringComparer.Ordinal))
				names.Add(name);
		}
		return names;
	}

	/// <summary>
	/// Tells a regular expression from a declaration-name query, so a regex sent in declaration
	/// mode is refused instead of answering a confident "no matches". No supported language puts
	/// a backslash, '^', '+', ".*" or "(?" in a declaration, and a declared name never holds a
	/// quantifier, bracket, brace or parenthesis: those may only follow the name as its parameter
	/// list, type arguments or body, as in "def foo(" or "interface Box&lt;T&gt; {". One trailing
	/// '?' stays part of a name, because Ruby declares predicates such as "valid?".
	/// </summary>
	public static bool LooksLikeRegularExpression(string pattern)
	{
		ArgumentNullException.ThrowIfNull(pattern);
		if (pattern.AsSpan().IndexOfAny(RegularExpressionOnlySyntax) >= 0 ||
			pattern.Contains(".*", StringComparison.Ordinal) ||
			pattern.Contains("(?", StringComparison.Ordinal))
		{
			return true;
		}

		foreach (var alternative in pattern.Split('|', StringSplitOptions.TrimEntries))
		{
			var name = FindDeclaredName(alternative);
			var looksLikeRegex = name.Length == 0
				? alternative.AsSpan().IndexOfAny(NameMetacharacters) >= 0
				: !IsPlainDeclaredName(name);
			if (looksLikeRegex)
				return true;
		}
		return false;
	}

	private static string FindDeclaredName(string alternative)
	{
		var words = alternative.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		for (var index = words.Length - 1; index >= 0; index--)
		{
			var word = words[index];
			var cut = word.IndexOfAny(DeclarationTailStarts);
			var name = (cut >= 0 ? word[..cut] : word).TrimEnd('.', '#', '/', ':');
			if (name.Length > 0)
				return name;
		}
		return string.Empty;
	}

	private static bool IsPlainDeclaredName(string name)
	{
		var body = name.Length > 1 && name[^1] == '?' && IsIdentifierCharacter(name[^2])
			? name.AsSpan(0, name.Length - 1)
			: name.AsSpan();
		return body.IndexOfAny(NameMetacharacters) < 0;
	}

	private static bool IsIdentifierCharacter(char character) =>
		char.IsLetterOrDigit(character) || character == '_';

	/// <summary>
	/// Builds the whole-identifier pattern for a declaration-name search. A qualified name is
	/// matched on its last segment here and narrowed to the qualified declaration afterwards.
	/// </summary>
	public static string ToDeclarationNamePattern(IReadOnlyList<string> names)
	{
		ArgumentNullException.ThrowIfNull(names);
		ArgumentOutOfRangeException.ThrowIfZero(names.Count);
		var alternatives = names
			.Select(static name => Regex.Escape(LastSearchSegment(name).ToString()))
			.Distinct(StringComparer.Ordinal);
		return $"(?<![\\p{{L}}\\p{{N}}_])(?:{string.Join('|', alternatives)})(?![\\p{{L}}\\p{{N}}_])";
	}

	/// <summary>
	/// Names the unshown tail of a declaration body, so a reader asks for exactly those lines
	/// instead of rereading the whole declaration to be sure nothing was missed.
	/// </summary>
	public static string FormatBodyTruncationNotice(int remainingLines, int declarationEndLine, bool offerGetFile)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(remainingLines);
		var remaining = remainingLines.ToString(CultureInfo.InvariantCulture);
		var range = (declarationEndLine - remainingLines + 1).ToString(CultureInfo.InvariantCulture) + "-" +
			declarationEndLine.ToString(CultureInfo.InvariantCulture);
		return offerGetFile
			? $"[Declaration body truncated: {remaining} line(s) remain; read lines {range} with get_file.]"
			: $"[Declaration body truncated: {remaining} line(s) remain: lines {range}.]";
	}

	/// <summary>
	/// Finds the declarations named by <paramref name="requestedNames"/> in one file. A file whose
	/// text never contains any of the names cannot declare them, so it is ruled out without a parse.
	/// </summary>
	public static IReadOnlyList<McpSearchMatchContext> FindNamedDeclarationMatches(
		DependencyFactsEngine engine,
		string relativePath,
		string content,
		McpSearchRegex regex,
		int contextLines,
		IReadOnlyList<TransformedTextRange> protectedRanges,
		IReadOnlyList<string> requestedNames,
		CancellationToken cancellationToken,
		out IReadOnlyList<NavigationDeclaration>? navigation)
	{
		ArgumentNullException.ThrowIfNull(regex);
		navigation = null;
		if (!regex.IsMatch(content))
			return [];
		navigation = CaptureNavigation(engine, relativePath, content, cancellationToken);
		return FindNamedDeclarationMatches(
			content,
			regex,
			contextLines,
			protectedRanges,
			navigation,
			requestedNames,
			cancellationToken);
	}

	private static NavigationDeclaration? FindNamedDeclarationAtLine(
		IReadOnlyList<NavigationDeclaration> declarations,
		int line,
		IReadOnlyList<string> requestedNames)
	{
		ArgumentNullException.ThrowIfNull(declarations);
		ArgumentNullException.ThrowIfNull(requestedNames);
		NavigationDeclaration? best = null;
		foreach (var declaration in declarations)
		{
			if (line < declaration.StartLine || line > declaration.EndLine ||
				!NameMatchesAny(declaration.Name, requestedNames))
			{
				continue;
			}

			if (best is null ||
				declaration.EndLine - declaration.StartLine < best.EndLine - best.StartLine ||
				declaration.EndLine - declaration.StartLine == best.EndLine - best.StartLine &&
				declaration.EndIndex - declaration.StartIndex < best.EndIndex - best.StartIndex)
			{
				best = declaration;
			}
		}
		return best;
	}

	public static IReadOnlyList<McpSearchMatchContext> FindNamedDeclarationMatches(
		string content,
		McpSearchRegex regex,
		int contextLines,
		IReadOnlyList<TransformedTextRange> protectedRanges,
		IReadOnlyList<NavigationDeclaration> declarations,
		IReadOnlyList<string> requestedNames,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(content);
		ArgumentNullException.ThrowIfNull(regex);
		ArgumentNullException.ThrowIfNull(protectedRanges);
		ArgumentNullException.ThrowIfNull(declarations);
		ArgumentNullException.ThrowIfNull(requestedNames);
		ArgumentOutOfRangeException.ThrowIfNegative(contextLines);

		var matching = declarations
			.Where(declaration => NameMatchesAny(declaration.Name, requestedNames))
			.ToArray();
		if (matching.Length == 0)
			return [];

		var contexts = new Dictionary<NavigationDeclaration, McpSearchMatchContext>();
		McpSearchTextScanner.ScanEach(
			content,
			regex,
			contextLines,
			protectedRanges,
			match =>
			{
				var declaration = FindNamedDeclarationAtLine(
					matching,
					match.MatchLineNumber,
					requestedNames);
				if (declaration is not null)
					contexts.TryAdd(declaration, match);
			},
			cancellationToken);

		if (contexts.Count < matching.Length)
		{
			var lines = ReadLineRanges(content, cancellationToken);
			foreach (var declaration in matching)
			{
				if (contexts.ContainsKey(declaration))
					continue;
				var firstLine = Math.Max(1, declaration.StartLine - contextLines);
				var lastLine = checked(declaration.StartLine + contextLines);
				var evidence = lines
					.Where(line => line.LineNumber >= firstLine && line.LineNumber <= lastLine)
					.ToArray();
				if (evidence.Length > 0)
				{
					contexts[declaration] = new McpSearchMatchContext(
						[declaration.StartLine],
						evidence,
						StartsNewGroup: false);
				}
			}
		}

		return matching
			.Where(contexts.ContainsKey)
			.Select(declaration => contexts[declaration])
			.ToArray();
	}

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
		return engine.ExtractNavigationFromProtectedText(relativePath, transformedText, fingerprint, cancellationToken);
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
			return McpSymbolLookup.Ambiguous(ToCandidateLines(qualified));

		var ownerQualified = spans
			.Where(span => span.Name.EndsWith('.' + symbol, StringComparison.Ordinal))
			.ToArray();
		if (ownerQualified.Length == 1)
			return McpSymbolLookup.Found(ownerQualified[0].Start, ownerQualified[0].End);
		if (ownerQualified.Length > 1)
			return McpSymbolLookup.Ambiguous(ToCandidateLines(ownerQualified));

		var simple = spans.Where(span => LastSegment(span.Name).Equals(symbol, StringComparison.Ordinal)).ToArray();
		return simple.Length switch
		{
			1 => McpSymbolLookup.Found(simple[0].Start, simple[0].End),
			> 1 => McpSymbolLookup.Ambiguous(ToCandidateLines(simple)),
			_ => McpSymbolLookup.Unknown
		};
	}

	private static (int StartLine, int EndLine)[] ToCandidateLines(DeclarationSpan[] spans) =>
		spans
			.OrderBy(static span => span.Start)
			.ThenBy(static span => span.End)
			.Select(static span => (span.Start, span.End))
			.ToArray();

	private static ReadOnlySpan<char> LastSegment(string name)
	{
		var separator = name.AsSpan().LastIndexOfAny('.', '#', '/');
		return separator >= 0 ? name.AsSpan(separator + 1) : name.AsSpan();
	}

	private static bool NameMatchesAny(string declaredName, IReadOnlyList<string> requestedNames)
	{
		foreach (var requestedName in requestedNames)
		{
			if (NameMatches(declaredName, requestedName))
				return true;
		}
		return false;
	}

	private static bool NameMatches(string declaredName, string requestedName)
	{
		if (declaredName.Equals(requestedName, StringComparison.OrdinalIgnoreCase))
			return true;
		return requestedName.IndexOfAny(['.', '#', '/', ':']) < 0 &&
			   LastSearchSegment(declaredName).Equals(requestedName, StringComparison.OrdinalIgnoreCase);
	}

	private static ReadOnlySpan<char> LastSearchSegment(string name)
	{
		var separator = name.LastIndexOfAny(['.', '#', '/', ':']);
		return separator >= 0 ? name.AsSpan(separator + 1) : name.AsSpan();
	}

	private static IReadOnlyList<McpTextLineRange> ReadLineRanges(
		string content,
		CancellationToken cancellationToken)
	{
		var lines = new List<McpTextLineRange>();
		var lineStart = 0;
		var lineNumber = 1;
		for (var index = 0; index < content.Length; index++)
		{
			if ((index & 0xFFF) == 0)
				cancellationToken.ThrowIfCancellationRequested();
			if (content[index] is not ('\r' or '\n'))
				continue;
			lines.Add(new McpTextLineRange(lineNumber++, lineStart, index - lineStart));
			if (content[index] == '\r' && index + 1 < content.Length && content[index + 1] == '\n')
				index++;
			lineStart = index + 1;
		}
		lines.Add(new McpTextLineRange(lineNumber, lineStart, content.Length - lineStart));
		return lines;
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
	IReadOnlyList<(int StartLine, int EndLine)> Candidates)
{
	private const int MaximumListedCandidates = 6;

	public static readonly McpSymbolLookup Unknown = new(McpSymbolLookupStatus.Unknown, 0, 0, []);
	public static readonly McpSymbolLookup Unsupported = new(McpSymbolLookupStatus.Unsupported, 0, 0, []);

	public int CandidateCount => Candidates.Count;

	public static McpSymbolLookup Found(int startLine, int endLine) =>
		new(McpSymbolLookupStatus.Resolved, startLine, endLine, [(startLine, endLine)]);

	public static McpSymbolLookup Ambiguous(IReadOnlyList<(int StartLine, int EndLine)> candidates) =>
		new(McpSymbolLookupStatus.Ambiguous, 0, 0, candidates);

	/// <summary>
	/// Lists the candidates' line ranges in file order, so an ambiguous name can be read by range
	/// instead of by reading the whole file. Line numbers are not project text.
	/// </summary>
	public string FormatCandidateLines()
	{
		var listed = string.Join(
			", ",
			Candidates
				.Take(MaximumListedCandidates)
				.Select(static candidate => string.Create(
					CultureInfo.InvariantCulture,
					$"{candidate.StartLine}-{candidate.EndLine}")));
		var unlisted = Candidates.Count - MaximumListedCandidates;
		return unlisted > 0
			? string.Create(CultureInfo.InvariantCulture, $"{listed} and {unlisted} more")
			: listed;
	}
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
	bool IsAddressable,
	IReadOnlyList<McpSearchProtectedLine>? ProtectedLines = null);

internal sealed class McpSearchDeclarationPreviewCache
{
	private readonly string relativePath;
	private readonly string content;
	private readonly IReadOnlyDictionary<string, int> declarationNameCounts;
	private readonly int maximumCharacters;
	private readonly CancellationToken cancellationToken;
	private readonly IReadOnlyDictionary<int, int>? protectionCountsByLine;
	private readonly Dictionary<DeclarationIdentity, McpSearchDeclarationPreview> previews = [];
	private int[]? lineStarts;

	public McpSearchDeclarationPreviewCache(
		string relativePath,
		string content,
		IReadOnlyList<NavigationDeclaration> declarations,
		int maximumCharacters,
		CancellationToken cancellationToken,
		IReadOnlyDictionary<int, int>? protectionCountsByLine = null)
	{
		this.relativePath = relativePath;
		this.content = content;
		declarationNameCounts = CountDeclarationNames(declarations);
		this.maximumCharacters = maximumCharacters;
		this.cancellationToken = cancellationToken;
		this.protectionCountsByLine = protectionCountsByLine;
	}

	public McpSearchDeclarationPreview Get(NavigationDeclaration declaration)
	{
		var identity = new DeclarationIdentity(declaration.Name, declaration.StartLine, declaration.EndLine);
		if (previews.TryGetValue(identity, out var preview))
			return preview;

		lineStarts ??= BuildLineStarts(content, cancellationToken);
		var body = Slice(declaration.StartLine, declaration.EndLine);
		var displayedEndLine = declaration.EndLine - body.RemainingLines;
		var protectedLines = protectionCountsByLine is null
			? null
			: protectionCountsByLine
				.Where(item => item.Key >= declaration.StartLine && item.Key <= displayedEndLine)
				.Select(static item => new McpSearchProtectedLine(item.Key, item.Value))
				.ToArray();
		preview = new McpSearchDeclarationPreview(
			new McpSearchDeclaration(
				relativePath,
				declaration.Name,
				declaration.StartLine,
				declaration.EndLine),
			body.Text,
			body.RemainingLines,
			declarationNameCounts[declaration.Name] == 1,
			protectedLines);
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
