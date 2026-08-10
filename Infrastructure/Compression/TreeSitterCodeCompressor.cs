using DevProjex.Application.Compression;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using TreeSitter;

namespace DevProjex.Infrastructure.Compression;

/// <summary>One language this build can compress, as seen from outside Infrastructure.</summary>
public sealed record CompressionLanguageInfo(
	string Id,
	string DisplayName,
	IReadOnlyList<string> Extensions,
	string GrammarLibrary,
	string GrammarExport);

/// <summary>
/// Builds compression plans with tree-sitter. Everything language-specific is data: the grammar,
/// the queries and the placeholder come from <see cref="CompressionLanguagePack"/>.
/// </summary>
public sealed class TreeSitterCodeCompressor :
	ICodeCompressor,
	ICodeCompressionRuntimeDiagnosticsProvider,
	IDisposable
{
	/// <summary>
	/// Parsing cannot be interrupted. tree-sitter 0.26.3 removed ts_parser_set_timeout_micros and
	/// ts_parser_set_cancellation_flag, and the binding does not expose the replacement
	/// ts_parser_parse_with_options, so a pathological file can neither be bounded nor aborted once
	/// started. A size cap before parsing is the only defence there is; files above it are reported
	/// as UnchangedTooLarge so the user sees a reason rather than an unexplained refusal.
	/// </summary>
	public const int MaximumParsableCharacters = 2 * 1024 * 1024;
	internal const uint MaximumQueryMatchLimit = 4096;
	internal const int MaximumBodyCapturesPerFile = 20_000;
	internal const int MaximumPreservedRangesPerFile = 20_000;
	internal const int MaximumEditsPerFile = 10_000;
	internal const int MaximumDeclarationsPerFile = 25_000;
	internal const int MaximumDefectsPerFile = 4_096;
	internal const int MaximumVisitedSyntaxNodesPerFile = 500_000;

	private readonly IGrammarLibraryLocator _locator;
	private readonly IReadOnlyList<CompressionLanguagePack> _packs;
	private readonly Dictionary<string, IReadOnlyList<CompressionLanguagePack>> _byExtension;
	private readonly Dictionary<string, LanguageWorkerPool> _languagePools = [];
	private readonly LanguageWorkerBudget _workerBudget;
	private readonly uint _queryMatchLimit;
	private readonly object _lifetimeSync = new();
	private long _nextScopeId;
	private int _activeScopes;
	private bool _disposeRequested;
	private bool _resourcesDisposed;

	public TreeSitterCodeCompressor(IGrammarLibraryLocator locator)
		: this(locator, CompressionLanguagePack.LoadAll())
	{
	}

	internal TreeSitterCodeCompressor(
		IGrammarLibraryLocator locator,
		IReadOnlyList<CompressionLanguagePack> packs,
		uint queryMatchLimit = MaximumQueryMatchLimit)
	{
		ArgumentOutOfRangeException.ThrowIfZero(queryMatchLimit);
		_locator = locator;
		_packs = packs;
		_queryMatchLimit = queryMatchLimit;
		_workerBudget = new LanguageWorkerBudget(
			packs.Select(static pack => pack.Id).Distinct(StringComparer.Ordinal).Count());
		_byExtension = packs
			.SelectMany(pack => pack.Extensions.Select(extension => (extension, pack)))
			.GroupBy(static pair => pair.extension, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(
				static group => group.Key,
				static group => (IReadOnlyList<CompressionLanguagePack>)group
					.Select(static pair => pair.pack)
					.OrderBy(static pack => pack.Id, StringComparer.Ordinal)
					.ToArray(),
				StringComparer.OrdinalIgnoreCase);
		TransformIdentity = "tree-sitter:" + string.Join(",", packs.Select(static pack => pack.Identity));
	}

	public string TransformIdentity { get; }

	public int AnalysisWorkerCapacity => _workerBudget.Diagnostics.Capacity;

	public CodeCompressionRuntimeDiagnosticSnapshot CaptureRuntimeDiagnostics()
	{
		var diagnostics = RuntimeDiagnostics;
		return new CodeCompressionRuntimeDiagnosticSnapshot(
			diagnostics.CompiledQuerySets,
			diagnostics.MaterializedWorkers,
			diagnostics.AvailableWorkers,
			diagnostics.LeasedWorkers,
			diagnostics.GlobalWorkerCapacity,
			diagnostics.GlobalActiveWorkers,
			diagnostics.GlobalPeakActiveWorkers,
			diagnostics.GlobalRetainedWorkers,
			diagnostics.GlobalRetainedWorkerCapacity);
	}

	internal IReadOnlyList<CompressionLanguagePack> Packs => _packs;

	internal CodeCompressionRuntimeDiagnostics RuntimeDiagnostics
	{
		get
		{
			LanguagePoolDiagnostics[] diagnostics;
			lock (_lifetimeSync)
			{
				diagnostics = _languagePools.Values
					.Select(static pool => pool.Diagnostics)
					.ToArray();
			}
			var budget = _workerBudget.Diagnostics;
			return new CodeCompressionRuntimeDiagnostics(
				diagnostics.Sum(static value => value.CompiledQuerySets),
				diagnostics.Sum(static value => value.MaterializedWorkers),
				diagnostics.Sum(static value => value.AvailableWorkers),
				diagnostics.Sum(static value => value.LeasedWorkers),
				budget.Capacity,
				budget.Active,
				budget.PeakActive,
				budget.Retained,
				budget.RetainedCapacity);
		}
	}

	/// <summary>
	/// What this build can actually compress. Shipping a grammar is not the same as supporting a
	/// language, so this is the list the UI and the delivery check must both read - never a
	/// hard-coded one that can drift from what is embedded.
	/// </summary>
	public IReadOnlyList<CompressionLanguageInfo> Languages =>
		_packs.Select(static pack => new CompressionLanguageInfo(
			pack.Id,
			pack.DisplayName,
			pack.Extensions,
			pack.Library,
			pack.Export)).ToArray();

	public bool IsSupported(string relativePath) =>
		_byExtension.ContainsKey(Path.GetExtension(relativePath));

	public ICodeCompressionScope CreateScope(string projectRoot)
	{
		lock (_lifetimeSync)
		{
			ObjectDisposedException.ThrowIf(_disposeRequested, this);
			_activeScopes++;
			return new TreeSitterCompressionScope(
				_byExtension,
				TransformIdentity,
				Interlocked.Increment(ref _nextScopeId),
				GetLanguagePool,
				ReleaseScope);
		}
	}

	private LanguageWorkerPool GetLanguagePool(CompressionLanguagePack pack)
	{
		lock (_lifetimeSync)
		{
			ObjectDisposedException.ThrowIf(_resourcesDisposed, this);
			if (_languagePools.TryGetValue(pack.Id, out var existing))
				return existing;

			var created = new LanguageWorkerPool(
				_locator,
				pack,
				_workerBudget,
				queryMatchLimit: _queryMatchLimit);
			_languagePools.Add(pack.Id, created);
			return created;
		}
	}

	public void Dispose()
	{
		lock (_lifetimeSync)
		{
			if (_disposeRequested)
				return;
			_disposeRequested = true;
			if (_activeScopes != 0)
				return;
			DisposeResourcesLocked();
		}
	}

	private void ReleaseScope()
	{
		lock (_lifetimeSync)
		{
			if (_activeScopes > 0)
				_activeScopes--;
			if (_disposeRequested && _activeScopes == 0)
				DisposeResourcesLocked();
		}
	}

	private void DisposeResourcesLocked()
	{
		if (_resourcesDisposed)
			return;
		_resourcesDisposed = true;
		foreach (var pool in _languagePools.Values)
			pool.Dispose();
		_languagePools.Clear();
		_workerBudget.Dispose();
	}
}

/// <summary>
/// One output operation. It borrows process-lifetime parser workers lazily, only for languages that
/// actually appear; the scope itself owns no native parser and is safe for parallel file analysis.
/// </summary>
internal sealed class TreeSitterCompressionScope(
	IReadOnlyDictionary<string, IReadOnlyList<CompressionLanguagePack>> byExtension,
	string transformIdentity,
	long operationId,
	Func<CompressionLanguagePack, LanguageWorkerPool> languagePoolProvider,
	Action releaseScope) : ICodeCompressionScope
{
	private int _disposed;

	public CodeCompressionAnalysis Analyze(
		string fullPath,
		string relativePath,
		string content,
		CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

		if (!byExtension.TryGetValue(Path.GetExtension(relativePath), out var candidates))
			return Refused(relativePath, "unknown", CodeCompressionOutcome.UnchangedUnsupportedLanguage, content.Length);

		if (content.Length > TreeSitterCodeCompressor.MaximumParsableCharacters)
		{
			return Refused(
				relativePath,
				candidates[0].Id,
				CodeCompressionOutcome.UnchangedTooLarge,
				content.Length);
		}

		var pack = ResolveLanguagePack(candidates, relativePath, content);

		cancellationToken.ThrowIfCancellationRequested();

		LanguageWorkerLease lease;
		try
		{
			lease = languagePoolProvider(pack).Rent(operationId, cancellationToken);
		}
		catch (Exception exception) when (IsLanguageRuntimeFailure(exception))
		{
			return Refused(relativePath, pack.Id, CodeCompressionOutcome.UnchangedParseFailed, content.Length);
		}

		using var languageLease = lease;
		var language = languageLease.Worker;
		var source = content;
		var original = language.Parser.Parse(source);
		if (original is null)
			return Refused(relativePath, pack.Id, CodeCompressionOutcome.UnchangedParseFailed, content.Length);

		CodeCompressionPlan plan;
		List<CodeDeclaration>? originalDeclarations;
		List<CodeParseDefect>? originalDefects;
		try
		{
			var edits = CollectEdits(pack, language, original, source, cancellationToken);
			if (edits is null)
				return Refused(relativePath, pack.Id, CodeCompressionOutcome.UnchangedGateRejected, source.Length);
			try
			{
				plan = CodeCompressionPlan.Create(relativePath, pack.Id, edits, source.Length, transformIdentity);
			}
			catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
			{
				// A malformed pack must refuse one file rather than take down the output operation.
				return Refused(relativePath, pack.Id, CodeCompressionOutcome.UnchangedGateRejected, source.Length);
			}

			if (plan.Outcome != CodeCompressionOutcome.Compressed)
				return new CodeCompressionAnalysis(plan, null);

			originalDeclarations = ReadDeclarations(
				language,
				original.RootNode,
				ContentTransformMap.Identity);
			originalDefects = ReadDefects(original.RootNode, ContentTransformMap.Identity);
		}
		finally
		{
			language.ReleaseQueryTreeReferences();
			original.Dispose();
		}

		if (originalDeclarations is null || originalDefects is null)
		{
			return new CodeCompressionAnalysis(
				plan.ToUnchanged(CodeCompressionOutcome.UnchangedGateRejected),
				null);
		}

		var applied = plan.Apply(source);
		using var compressed = language.Parser.Parse(applied.Text);
		if (compressed is null)
			return new CodeCompressionAnalysis(
				plan.ToUnchanged(CodeCompressionOutcome.UnchangedGateRejected),
				null);

		var compressedDeclarations = ReadDeclarations(language, compressed.RootNode, applied.Map);
		var compressedDefects = ReadDefects(compressed.RootNode, applied.Map);
		language.ReleaseQueryTreeReferences();
		if (compressedDeclarations is null || compressedDefects is null)
		{
			return new CodeCompressionAnalysis(
				plan.ToUnchanged(CodeCompressionOutcome.UnchangedGateRejected),
				null);
		}

		var verdict = CodeStructureGate.Evaluate(
			originalDeclarations,
			compressedDeclarations,
			originalDefects,
			compressedDefects,
			plan.Edits,
			pack.ExecutableOwnerKinds);

		return verdict == CodeStructureGateVerdict.Accepted
			? new CodeCompressionAnalysis(plan, applied)
			: new CodeCompressionAnalysis(
				plan.ToUnchanged(CodeCompressionOutcome.UnchangedGateRejected),
				null);
	}

	private CodeCompressionAnalysis Refused(
		string relativePath,
		string languageId,
		CodeCompressionOutcome outcome,
		int length) =>
		new(CodeCompressionPlan.Unchanged(relativePath, languageId, outcome, length, transformIdentity), null);

	private static CompressionLanguagePack ResolveLanguagePack(
		IReadOnlyList<CompressionLanguagePack> candidates,
		string relativePath,
		string source)
	{
		if (candidates.Count == 1)
			return candidates[0];

		if (Path.GetExtension(relativePath).Equals(".h", StringComparison.OrdinalIgnoreCase))
		{
			var languageId = ContainsCppHeaderEvidence(source) ? "cpp" : "c";
			var selected = candidates.FirstOrDefault(candidate =>
				candidate.Id.Equals(languageId, StringComparison.Ordinal));
			if (selected is not null)
				return selected;
		}

		return candidates[0];
	}

	private static bool ContainsCppHeaderEvidence(string source)
	{
		var buffer = ArrayPool<char>.Shared.Rent(Math.Max(1, source.Length));
		try
		{
			var code = buffer.AsSpan(0, source.Length);
			StripCommentsStringsAndPreprocessor(source.AsSpan(), code);
			return ContainsWord(code, "namespace") ||
			       ContainsWord(code, "template") ||
			       ContainsWord(code, "class") ||
			       ContainsWord(code, "constexpr") ||
			       ContainsWord(code, "typename") ||
			       code.Contains("::", StringComparison.Ordinal) ||
			       ContainsAccessLabel(code, "public") ||
			       ContainsAccessLabel(code, "private") ||
			       ContainsAccessLabel(code, "protected") ||
			       ContainsInlineStructMethod(code);
		}
		finally
		{
			ArrayPool<char>.Shared.Return(buffer);
		}
	}

	private static void StripCommentsStringsAndPreprocessor(
		ReadOnlySpan<char> source,
		Span<char> destination)
	{
		destination.Fill(' ');
		var state = HeaderLexicalState.Code;
		var lineHasCode = false;
		var escaped = false;
		var lastNonWhitespace = '\0';
		for (var index = 0; index < source.Length; index++)
		{
			var current = source[index];
			var next = index + 1 < source.Length ? source[index + 1] : '\0';
			if (current == '\n')
			{
				destination[index] = current;
				var continuesLogicalLine =
					state is HeaderLexicalState.LineComment or HeaderLexicalState.Preprocessor &&
					lastNonWhitespace == '\\';
				if (!continuesLogicalLine &&
				    state is HeaderLexicalState.LineComment or HeaderLexicalState.Preprocessor)
				{
					state = HeaderLexicalState.Code;
				}
				lineHasCode = false;
				escaped = false;
				lastNonWhitespace = '\0';
				continue;
			}

			switch (state)
			{
				case HeaderLexicalState.LineComment:
				case HeaderLexicalState.Preprocessor:
					if (!char.IsWhiteSpace(current))
						lastNonWhitespace = current;
					continue;
				case HeaderLexicalState.BlockComment:
					if (current == '*' && next == '/')
					{
						state = HeaderLexicalState.Code;
						index++;
					}
					continue;
				case HeaderLexicalState.String:
					if (!escaped && current == '"')
						state = HeaderLexicalState.Code;
					escaped = !escaped && current == '\\';
					continue;
				case HeaderLexicalState.Character:
					if (!escaped && current == '\'')
						state = HeaderLexicalState.Code;
					escaped = !escaped && current == '\\';
					continue;
			}

			if (!lineHasCode && current == '#')
			{
				state = HeaderLexicalState.Preprocessor;
				continue;
			}
			if (current == '/' && next == '/')
			{
				state = HeaderLexicalState.LineComment;
				index++;
				continue;
			}
			if (current == '/' && next == '*')
			{
				state = HeaderLexicalState.BlockComment;
				index++;
				continue;
			}
			if (current == '"')
			{
				state = HeaderLexicalState.String;
				escaped = false;
				continue;
			}
			if (current == '\'')
			{
				state = HeaderLexicalState.Character;
				escaped = false;
				continue;
			}

			destination[index] = current;
			if (!char.IsWhiteSpace(current))
				lineHasCode = true;
		}
	}

	private static bool ContainsWord(ReadOnlySpan<char> code, ReadOnlySpan<char> word)
	{
		var offset = 0;
		while (offset <= code.Length - word.Length)
		{
			var relative = code[offset..].IndexOf(word, StringComparison.Ordinal);
			if (relative < 0)
				return false;
			var start = offset + relative;
			var end = start + word.Length;
			if ((start == 0 || !IsIdentifierCharacter(code[start - 1])) &&
			    (end == code.Length || !IsIdentifierCharacter(code[end])))
			{
				return true;
			}
			offset = start + 1;
		}
		return false;
	}

	private static bool ContainsAccessLabel(ReadOnlySpan<char> code, ReadOnlySpan<char> label)
	{
		var offset = 0;
		while (offset <= code.Length - label.Length)
		{
			var relative = code[offset..].IndexOf(label, StringComparison.Ordinal);
			if (relative < 0)
				return false;
			var start = offset + relative;
			var end = start + label.Length;
			if ((start == 0 || !IsIdentifierCharacter(code[start - 1])) &&
			    (end == code.Length || !IsIdentifierCharacter(code[end])))
			{
				while (end < code.Length && char.IsWhiteSpace(code[end]))
					end++;
				if (end < code.Length && code[end] == ':')
					return true;
			}
			offset = start + 1;
		}
		return false;
	}

	private static bool ContainsInlineStructMethod(ReadOnlySpan<char> code)
	{
		var searchOffset = 0;
		while (searchOffset < code.Length)
		{
			var relative = code[searchOffset..].IndexOf("struct", StringComparison.Ordinal);
			if (relative < 0)
				return false;
			var start = searchOffset + relative;
			var wordEnd = start + "struct".Length;
			searchOffset = start + 1;
			if ((start > 0 && IsIdentifierCharacter(code[start - 1])) ||
			    (wordEnd < code.Length && IsIdentifierCharacter(code[wordEnd])))
			{
				continue;
			}

			var open = code[wordEnd..].IndexOf('{');
			var terminator = code[wordEnd..].IndexOf(';');
			if (open < 0 || (terminator >= 0 && terminator < open))
				continue;
			open += wordEnd;
			var depth = 1;
			for (var index = open + 1; index < code.Length && depth > 0; index++)
			{
				if (code[index] == '{')
				{
					depth++;
					continue;
				}
				if (code[index] == '}')
				{
					depth--;
					continue;
				}
				if (depth != 1 || code[index] != ')')
					continue;
				if (HasInlineDefinitionAfter(code, index + 1))
					return true;
			}
		}
		return false;
	}

	private static bool HasInlineDefinitionAfter(ReadOnlySpan<char> code, int offset)
	{
		for (var index = offset; index < code.Length; index++)
		{
			if (code[index] == '{')
				return true;
			if (code[index] is ';' or '}')
				return false;
		}

		return false;
	}

	private static bool IsIdentifierCharacter(char value) =>
		char.IsAsciiLetterOrDigit(value) || value == '_';

	private enum HeaderLexicalState
	{
		Code,
		LineComment,
		BlockComment,
		String,
		Character,
		Preprocessor
	}

	private static List<CodeCompressionEdit>? CollectEdits(
		CompressionLanguagePack pack,
		LoadedLanguage language,
		Tree tree,
		string source,
		CancellationToken cancellationToken)
	{
		var preservedRanges = ReadPreservedRanges(language, tree.RootNode, cancellationToken);
		if (preservedRanges is null)
			return null;

		var bodyCaptures = new List<Node>();
		var expressionCaptures = new List<Node>();
		var captureCount = 0;
		language.BodiesCursor.Execute(language.Runtime.Bodies, tree.RootNode);
		foreach (var capture in language.BodiesCursor.Captures)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (capture.Name is not ("body" or "expression"))
				continue;
			if (captureCount >= TreeSitterCodeCompressor.MaximumBodyCapturesPerFile)
				return null;
			captureCount++;
			(capture.Name.Equals("body", StringComparison.Ordinal)
				? bodyCaptures
				: expressionCaptures).Add(capture.Node);
		}
		if (language.BodiesCursor.IsMatchLimitExceeded)
			return null;

		var raw = new List<RawCompressionEdit>(bodyCaptures.Count + expressionCaptures.Count);
		var bodyRanges = expressionCaptures.Count == 0
			? null
			: bodyCaptures
				.Select(static node => new SourceRange(node.StartIndex, node.EndIndex))
				.ToHashSet();
		foreach (var node in bodyCaptures)
		{

			// Defence in depth. The queries are anchored on the parent declaration's body: field, so
			// a container should be unreachable - but a grammar upgrade must fail loudly, not
			// silently delete a class body.
			if (pack.ContainerNodeTypes.Contains(node.Type))
				continue;
			if (IsMisparsedCppClassBody(pack, node, source))
				continue;

			var start = node.StartIndex;
			if (pack.PreserveLeadingDocstring)
			{
				if (!TryResolveContentAfterLeadingDocstring(
						node,
						source,
						out start,
						out var hasLeadingDocstring))
					continue;
				if (!hasLeadingDocstring)
					start = ExtendOverLeadingPythonComments(source, node);
			}

			if (start < 0 || node.EndIndex > source.Length || node.EndIndex <= start)
				continue;
			raw.Add(new RawCompressionEdit(
				start,
				node.EndIndex,
				PlaceholderFor(pack, source, start, node.EndIndex)));
		}

		foreach (var node in expressionCaptures)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (bodyRanges!.Contains(new SourceRange(node.StartIndex, node.EndIndex)))
				continue;
			if (node.StartPosition.Row == node.EndPosition.Row)
				continue;
			if (!TryResolveExpressionEdit(pack, node, source.Length, out var expressionEdit))
				return null;
			raw.Add(expressionEdit);
		}

		// Outermost wins: a lambda body inside a method body must not be spliced twice.
		raw.Sort(static (left, right) =>
			left.Start != right.Start
				? left.Start.CompareTo(right.Start)
				: right.End.CompareTo(left.End));
		var edits = new List<CodeCompressionEdit>(raw.Count);
		var reach = -1;
		var preservedIndex = 0;
		foreach (var (start, end, replacement) in raw)
		{
			if (start < reach)
				continue;
			while (preservedIndex < preservedRanges.Count && preservedRanges[preservedIndex].End <= start)
				preservedIndex++;
			if (preservedIndex < preservedRanges.Count &&
			    preservedRanges[preservedIndex].Start <= start &&
			    end <= preservedRanges[preservedIndex].End)
			{
				continue;
			}
			reach = end;
			if (edits.Count >= TreeSitterCodeCompressor.MaximumEditsPerFile)
				return null;
			edits.Add(new CodeCompressionEdit(start, end - start, replacement));
		}

		return edits;
	}

	private static bool TryResolveExpressionEdit(
		CompressionLanguagePack pack,
		Node expression,
		int sourceLength,
		out RawCompressionEdit edit)
	{
		var start = expression.StartIndex;
		var end = expression.EndIndex;
		switch (pack.ExpressionBodyStyle)
		{
			case ExpressionBodyStyle.Inline:
				break;
			case ExpressionBodyStyle.Declaration:
				var expressionContainer = expression.Parent;
				var owner = expressionContainer?.Parent;
				if (expressionContainer is null ||
				    owner is null ||
				    !pack.ExecutableOwnerKinds.Contains(owner.Type))
				{
					edit = default;
					return false;
				}

				start = expressionContainer.StartIndex;
				end = owner.EndIndex;
				break;
			default:
				edit = default;
				return false;
		}

		if (start < 0 || end > sourceLength || end <= start)
		{
			edit = default;
			return false;
		}

		edit = new RawCompressionEdit(start, end, pack.BlockPlaceholder);
		return true;
	}

	private static bool TryResolveContentAfterLeadingDocstring(
		Node body,
		string source,
		out int start,
		out bool hasLeadingDocstring)
	{
		start = body.StartIndex;
		hasLeadingDocstring = false;
		Node? firstStatement = null;
		Node? secondStatement = null;
		foreach (var child in body.Children)
		{
			if (!child.IsNamed)
				continue;
			if (firstStatement is null)
			{
				firstStatement = child;
				continue;
			}

			secondStatement = child;
			break;
		}

		if (firstStatement is null || !IsSimpleStringStatement(firstStatement, source))
			return true;
		hasLeadingDocstring = true;
		if (secondStatement is null)
			return false;

		start = secondStatement.StartIndex;
		return true;
	}

	private static bool IsSimpleStringStatement(Node statement, string source)
	{
		var stringNode = statement.Type.Equals("string", StringComparison.Ordinal)
			? statement
			: statement.Type.Equals("expression_statement", StringComparison.Ordinal)
				? statement.Children.FirstOrDefault(static child => child.IsNamed)
				: null;
		if (stringNode is null || !stringNode.Type.Equals("string", StringComparison.Ordinal))
			return false;
		if (stringNode.StartIndex < 0 ||
		    stringNode.EndIndex > source.Length ||
		    stringNode.EndIndex <= stringNode.StartIndex)
		{
			return false;
		}

		var text = source.AsSpan(stringNode.StartIndex, stringNode.EndIndex - stringNode.StartIndex).TrimStart();
		var quoteIndex = text.IndexOfAny('\'', '"');
		if (quoteIndex < 0)
			return false;
		var prefix = text[..quoteIndex];
		return !prefix.Contains('f') &&
		       !prefix.Contains('F') &&
		       !prefix.Contains('b') &&
		       !prefix.Contains('B');
	}

	private static IReadOnlyList<SourceRange>? ReadPreservedRanges(
		LoadedLanguage language,
		Node root,
		CancellationToken cancellationToken)
	{
		if (language.Runtime.Preserves is null || language.PreservesCursor is null)
			return Array.Empty<SourceRange>();

		List<SourceRange>? ranges = null;
		language.PreservesCursor.Execute(language.Runtime.Preserves, root);
		foreach (var capture in language.PreservesCursor.Captures)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!capture.Name.Equals("preserve", StringComparison.Ordinal))
				continue;
			if (ranges?.Count >= TreeSitterCodeCompressor.MaximumPreservedRangesPerFile)
				return null;
			if (capture.Node.StartIndex < 0 || capture.Node.EndIndex <= capture.Node.StartIndex)
				continue;
			(ranges ??= []).Add(new SourceRange(capture.Node.StartIndex, capture.Node.EndIndex));
		}
		if (language.PreservesCursor.IsMatchLimitExceeded)
			return null;
		if (ranges is null)
			return Array.Empty<SourceRange>();
		if (ranges.Count < 2)
			return ranges;

		ranges.Sort(static (left, right) =>
			left.Start != right.Start
				? left.Start.CompareTo(right.Start)
				: right.End.CompareTo(left.End));
		var writeIndex = 0;
		for (var readIndex = 1; readIndex < ranges.Count; readIndex++)
		{
			var current = ranges[writeIndex];
			var next = ranges[readIndex];
			if (next.Start <= current.End)
			{
				ranges[writeIndex] = new SourceRange(current.Start, Math.Max(current.End, next.End));
				continue;
			}

			ranges[++writeIndex] = next;
		}
		ranges.RemoveRange(writeIndex + 1, ranges.Count - writeIndex - 1);
		return ranges;
	}

	private readonly record struct SourceRange(int Start, int End);

	private readonly record struct RawCompressionEdit(int Start, int End, string Replacement);

	private static bool IsMisparsedCppClassBody(
		CompressionLanguagePack pack,
		Node node,
		string source)
	{
		if (!pack.Id.Equals("cpp", StringComparison.Ordinal) ||
		    node.Parent is not { Type: "function_definition" } function)
		{
			return false;
		}

		if (node.Children.Any(child => IsCppAccessLabel(child, source)))
			return true;

		var declarator = function.GetChildForField("declarator");
		return declarator is null ||
		       declarator.EndIndex > node.StartIndex ||
		       !ContainsNodeTypeBefore(declarator, "parameter_list", node.StartIndex);
	}

	private static bool IsCppAccessLabel(Node node, string source)
	{
		if (!node.Type.Equals("labeled_statement", StringComparison.Ordinal) ||
		    node.StartIndex < 0 ||
		    node.EndIndex > source.Length)
		{
			return false;
		}

		var label = source.AsSpan(node.StartIndex, Math.Min(node.EndIndex - node.StartIndex, 16)).TrimStart();
		return label.StartsWith("public:", StringComparison.Ordinal) ||
		       label.StartsWith("private:", StringComparison.Ordinal) ||
		       label.StartsWith("protected:", StringComparison.Ordinal);
	}

	private static bool ContainsNodeTypeBefore(Node root, string nodeType, int maximumEnd)
	{
		var stack = new Stack<Node>();
		stack.Push(root);
		while (stack.Count > 0)
		{
			var node = stack.Pop();
			if (node.EndIndex <= maximumEnd && node.Type.Equals(nodeType, StringComparison.Ordinal))
				return true;
			foreach (var child in node.Children)
				stack.Push(child);
		}

		return false;
	}

	/// <summary>
	/// Python comments before the first statement are extra nodes outside the block captured by the
	/// query. Include only those complete lines, never preprocessor directives from other languages.
	/// </summary>
	private static int ExtendOverLeadingPythonComments(string source, Node body)
	{
		var limit = body.Parent?.StartIndex ?? 0;
		var start = body.StartIndex;
		while (true)
		{
			var lineStart = source.LastIndexOf('\n', Math.Max(0, start - 1));
			if (lineStart < 0 || lineStart <= limit)
				return start;

			var previousLineStart = source.LastIndexOf('\n', Math.Max(0, lineStart - 1));
			if (previousLineStart < limit)
				return start;

			var line = source[(previousLineStart + 1)..lineStart].Trim();
			if (line.Length == 0 || !line.StartsWith('#'))
				return start;

			start = previousLineStart + 1;
		}
	}

	/// <summary>
	/// A placeholder must be valid syntax: the reverse-parse gate refuses anything that does not
	/// parse. Empty blocks are deliberately neutral and avoid exposing implementation-style comment
	/// markers in the generated context. Python uses its valid ellipsis statement instead.
	/// A retained Python docstring moves the edit to the first executable statement, so the same
	/// indentation logic works for documented and undocumented functions.
	/// </summary>
	private static string PlaceholderFor(CompressionLanguagePack pack, string source, int start, int end)
	{
		if (!pack.Id.Equals("python", StringComparison.Ordinal))
			return pack.BlockPlaceholder;

		// Python: an indented "..." keeps the suite non-empty. The indentation of the first removed
		// line is reused so the result stays parseable.
		var lineStart = source.LastIndexOf('\n', Math.Max(0, Math.Min(start, source.Length - 1)));
		var indentation = new string(' ', 4);
		if (lineStart >= 0 && lineStart + 1 < source.Length)
		{
			var scan = lineStart + 1;
			while (scan < source.Length && (source[scan] == ' ' || source[scan] == '\t'))
				scan++;
			if (scan > lineStart + 1)
				indentation = source[(lineStart + 1)..scan];
		}

		var lineEnding = ResolveLineEnding(source, lineStart);
		var trailingNewline = end > 0 && end <= source.Length && source[end - 1] == '\n'
			? end > 1 && source[end - 2] == '\r' ? "\r\n" : "\n"
			: string.Empty;

		// What the retained text already ends with decides what the placeholder has to supply. A
		// tree-sitter block starts at its first statement, so the newline and indentation before it
		// are kept - emitting them again would leave a blank, whitespace-only line above the "...".
		// After the edit was extended back over leading comments the start sits at column zero, and
		// only the indentation is missing.
		var retainedEndsAtLineStart = start > 0 && source[start - 1] == '\n';
		var retainedEndsWithIndentation =
			!retainedEndsAtLineStart &&
			lineStart >= 0 &&
			start > lineStart + 1 &&
			source.AsSpan((lineStart + 1)..start).IsWhiteSpace();
		var leading = retainedEndsWithIndentation
			? string.Empty
			: retainedEndsAtLineStart
				? indentation
				: $"{lineEnding}{indentation}";
		return $"{leading}{pack.BlockPlaceholder}{trailingNewline}";
	}

	private static string ResolveLineEnding(string source, int precedingLineFeed)
	{
		if (precedingLineFeed >= 0)
			return precedingLineFeed > 0 && source[precedingLineFeed - 1] == '\r' ? "\r\n" : "\n";

		var nextLineFeed = source.IndexOf('\n');
		return nextLineFeed > 0 && source[nextLineFeed - 1] == '\r' ? "\r\n" : "\n";
	}

	private static List<CodeDeclaration>? ReadDeclarations(LoadedLanguage language, Node root, ContentTransformMap map)
	{
		var declarations = new List<CodeDeclaration>();
		language.DeclarationsCursor.Execute(language.Runtime.Declarations, root);
		foreach (var match in language.DeclarationsCursor.Matches)
		{
			Node? declaration = null;
			string? name = null;
			foreach (var capture in match.Captures)
			{
				if (capture.Name.Equals("declaration", StringComparison.Ordinal))
					declaration = capture.Node;
				else if (capture.Name.Equals("name", StringComparison.Ordinal))
					name = capture.Node.Text;
			}

			if (declaration is null)
				continue;
			if (declarations.Count >= TreeSitterCodeCompressor.MaximumDeclarationsPerFile)
				return null;
			if (!map.TryToSource(declaration.StartIndex, out var start))
				continue;
			if (!map.TryToSource(declaration.EndIndex, out var end))
				end = start;
			declarations.Add(new CodeDeclaration(declaration.Type, name ?? string.Empty, start, Math.Max(0, end - start)));
		}
		if (language.DeclarationsCursor.IsMatchLimitExceeded)
			return null;

		declarations.Sort(static (left, right) =>
		{
			var result = left.Start.CompareTo(right.Start);
			if (result != 0)
				return result;

			result = string.CompareOrdinal(left.Kind, right.Kind);
			return result != 0 ? result : string.CompareOrdinal(left.Name, right.Name);
		});
		return declarations;
	}

	private static List<CodeParseDefect>? ReadDefects(Node root, ContentTransformMap map)
	{
		var defects = new List<CodeParseDefect>();
		using var cursor = new TreeCursor(root);
		var visitedNodes = 0;
		while (true)
		{
			if (++visitedNodes > TreeSitterCodeCompressor.MaximumVisitedSyntaxNodesPerFile)
				return null;
			var node = cursor.CurrentNode;
			// See CodeParseDefect: HasError lies in both directions against the shipped grammars,
			// and a MISSING node surfaces here only as a named node of zero width.
			var isDefect = node.IsError || node.IsMissing || (node.IsNamed && node.StartIndex == node.EndIndex);
			if (isDefect)
			{
				if (defects.Count >= TreeSitterCodeCompressor.MaximumDefectsPerFile)
					return null;
				var kind = node.IsError ? "ERROR" : "MISSING";
				defects.Add(new CodeParseDefect(kind, map.TryToSource(node.StartIndex, out var source) ? source : -1));
			}

			if (cursor.GotoFirstChild())
				continue;

			while (!cursor.GotoNextSibling())
			{
				if (!cursor.GotoParent())
					return defects;
			}
		}
	}

	private static bool IsLanguageRuntimeFailure(Exception exception) =>
		exception is DllNotFoundException or
			InvalidOperationException or
			IOException or
			UnauthorizedAccessException or
			BadImageFormatException or
			EntryPointNotFoundException;

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) == 0)
			releaseScope();
	}
}

internal readonly record struct CodeCompressionRuntimeDiagnostics(
	int CompiledQuerySets,
	int MaterializedWorkers,
	int AvailableWorkers,
	int LeasedWorkers,
	int GlobalWorkerCapacity,
	int GlobalActiveWorkers,
	int GlobalPeakActiveWorkers,
	int GlobalRetainedWorkers,
	int GlobalRetainedWorkerCapacity);

/// <summary>
/// Bounded process-lifetime parser pool. Grammar verification and materialization happen once;
/// native parser state is never entered concurrently.
/// </summary>
internal sealed class LanguageWorkerPool : IDisposable
{
	private const int MaximumRetainedWorkers = 2;
	private readonly IGrammarLibraryLocator _locator;
	private readonly CompressionLanguagePack _pack;
	private readonly LanguageWorkerBudget _workerBudget;
	private readonly bool _ownsWorkerBudget;
	private readonly uint _queryMatchLimit;
	private readonly ConcurrentBag<LoadedLanguage> _available = [];
	private readonly object _sync = new();
	private LanguageRuntime? _runtime;
	private ExceptionDispatchInfo? _permanentRuntimeFailure;
	private ExceptionDispatchInfo? _transientRuntimeFailure;
	private long _transientFailureOperationId = -1;
	private int _leasedWorkers;
	private int _materializedWorkers;
	private bool _disposed;

	public LanguageWorkerPool(IGrammarLibraryLocator locator, CompressionLanguagePack pack)
		: this(locator, pack, new LanguageWorkerBudget(), ownsWorkerBudget: true)
	{
	}

	internal LanguageWorkerPool(
		IGrammarLibraryLocator locator,
		CompressionLanguagePack pack,
		LanguageWorkerBudget workerBudget,
		bool ownsWorkerBudget = false,
		uint queryMatchLimit = TreeSitterCodeCompressor.MaximumQueryMatchLimit)
	{
		ArgumentOutOfRangeException.ThrowIfZero(queryMatchLimit);
		_locator = locator;
		_pack = pack;
		_workerBudget = workerBudget;
		_ownsWorkerBudget = ownsWorkerBudget;
		_queryMatchLimit = queryMatchLimit;
	}

	internal LanguagePoolDiagnostics Diagnostics
	{
		get
		{
			lock (_sync)
			{
				return new LanguagePoolDiagnostics(
					_runtime is not null ? 1 : 0,
					_materializedWorkers,
					_available.Count,
					_leasedWorkers);
			}
		}
	}

	public LanguageWorkerLease Rent(CancellationToken cancellationToken) =>
		Rent(operationId: 0, cancellationToken);

	internal LanguageWorkerLease Rent(long operationId, CancellationToken cancellationToken)
	{
		var budgetLease = _workerBudget.Rent(cancellationToken);
		LoadedLanguage? worker = null;
		var leaseRegistered = false;
		try
		{
			lock (_sync)
			{
				ObjectDisposedException.ThrowIf(_disposed, this);
				_leasedWorkers++;
				leaseRegistered = true;
				if (_available.TryTake(out worker))
					_workerBudget.ReleaseRetained();
			}

			if (worker is null)
			{
				worker = LoadedLanguage.Create(
					GetOrCreateRuntime(operationId),
					_queryMatchLimit);
				lock (_sync)
					_materializedWorkers++;
			}
			lock (_sync)
				ObjectDisposedException.ThrowIf(_disposed, this);
			return new LanguageWorkerLease(this, worker, budgetLease);
		}
		catch
		{
			if (worker is not null)
			{
				worker.Dispose();
				lock (_sync)
					_materializedWorkers--;
			}
			LanguageRuntime? runtimeToDispose;
			lock (_sync)
			{
				if (leaseRegistered)
					_leasedWorkers--;
				runtimeToDispose = TakeRuntimeForDisposalLocked();
			}
			runtimeToDispose?.Dispose();
			budgetLease.Dispose();
			throw;
		}
	}

	internal void Return(LoadedLanguage worker, IDisposable budgetLease)
	{
		var reusable = true;
		try
		{
			worker.PrepareForReturn();
		}
		catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
		{
			reusable = false;
		}

		var disposeWorker = !reusable;
		LanguageRuntime? runtimeToDispose;
		lock (_sync)
		{
			_leasedWorkers--;
			if (_disposed)
				disposeWorker = true;
			else if (reusable &&
			         _available.Count < MaximumRetainedWorkers &&
			         _workerBudget.TryRetain())
			{
				_available.Add(worker);
			}
			else
			{
				disposeWorker = true;
			}
			if (disposeWorker)
				_materializedWorkers--;
			runtimeToDispose = TakeRuntimeForDisposalLocked();
		}

		if (disposeWorker)
			worker.Dispose();
		budgetLease.Dispose();
		runtimeToDispose?.Dispose();
	}

	public void Dispose()
	{
		List<LoadedLanguage> workers;
		LanguageRuntime? runtimeToDispose;
		lock (_sync)
		{
			if (_disposed)
				return;
			_disposed = true;
			workers = new List<LoadedLanguage>(_available.Count);
			while (_available.TryTake(out var worker))
				workers.Add(worker);
			_materializedWorkers -= workers.Count;
			runtimeToDispose = TakeRuntimeForDisposalLocked();
		}

		foreach (var worker in workers)
			worker.Dispose();
		_workerBudget.ReleaseRetained(workers.Count);
		if (_ownsWorkerBudget)
			_workerBudget.Dispose();
		runtimeToDispose?.Dispose();
	}

	private LanguageRuntime GetOrCreateRuntime(long operationId)
	{
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_runtime is not null)
				return _runtime;
			_permanentRuntimeFailure?.Throw();
			if (_transientFailureOperationId == operationId)
				_transientRuntimeFailure?.Throw();

			try
			{
				_runtime = LanguageRuntime.Create(
					_locator.Resolve(_pack.Library),
					_pack);
				_transientRuntimeFailure = null;
				_transientFailureOperationId = -1;
				return _runtime;
			}
			catch (Exception exception) when (IsTransientInitializationFailure(exception))
			{
				_transientRuntimeFailure = ExceptionDispatchInfo.Capture(exception);
				_transientFailureOperationId = operationId;
				throw;
			}
			catch (Exception exception)
			{
				_permanentRuntimeFailure = ExceptionDispatchInfo.Capture(exception);
				throw;
			}
		}
	}

	private static bool IsTransientInitializationFailure(Exception exception) =>
		exception is IOException or UnauthorizedAccessException or DllNotFoundException;

	private LanguageRuntime? TakeRuntimeForDisposalLocked()
	{
		if (!_disposed ||
		    _leasedWorkers != 0 ||
		    _runtime is null)
		{
			return null;
		}

		var runtime = _runtime;
		_runtime = null;
		return runtime;
	}
}

internal readonly record struct LanguagePoolDiagnostics(
	int CompiledQuerySets,
	int MaterializedWorkers,
	int AvailableWorkers,
	int LeasedWorkers);

internal sealed class LanguageWorkerLease(
	LanguageWorkerPool owner,
	LoadedLanguage worker,
	IDisposable budgetLease) : IDisposable
{
	private LanguageWorkerPool? _owner = owner;

	public LoadedLanguage Worker { get; } = worker;

	public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Return(Worker, budgetLease);
}

internal sealed class LanguageWorkerBudget : IDisposable
{
	private const int MaximumActiveWorkers = 8;
	private readonly SemaphoreSlim _capacity;
	private readonly CancellationTokenSource _shutdown = new();
	private int _active;
	private int _peakActive;
	private int _retained;
	private int _disposed;

	public LanguageWorkerBudget(int minimumRetainedWorkers = 1)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumRetainedWorkers);
		Capacity = Math.Clamp(Environment.ProcessorCount, 1, MaximumActiveWorkers);
		RetainedCapacity = Math.Max(minimumRetainedWorkers, Capacity * 2);
		_capacity = new SemaphoreSlim(Capacity, Capacity);
	}

	public int Capacity { get; }
	public int RetainedCapacity { get; }

	internal LanguageWorkerBudgetDiagnostics Diagnostics => new(
		Capacity,
		Volatile.Read(ref _active),
		Volatile.Read(ref _peakActive),
		Volatile.Read(ref _retained),
		RetainedCapacity);

	public bool TryRetain()
	{
		while (true)
		{
			var retained = Volatile.Read(ref _retained);
			if (retained >= RetainedCapacity)
				return false;
			if (Interlocked.CompareExchange(ref _retained, retained + 1, retained) == retained)
				return true;
		}
	}

	public void ReleaseRetained(int count = 1)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(count);
		if (count == 0)
			return;
		var retained = Interlocked.Add(ref _retained, -count);
		if (retained < 0)
			throw new InvalidOperationException("The retained worker budget was released more than it was acquired.");
	}

	public IDisposable Rent(CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(
			cancellationToken,
			_shutdown.Token);
		try
		{
			_capacity.Wait(linked.Token);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new ObjectDisposedException(nameof(LanguageWorkerBudget));
		}

		if (Volatile.Read(ref _disposed) != 0)
		{
			_capacity.Release();
			throw new ObjectDisposedException(nameof(LanguageWorkerBudget));
		}

		var active = Interlocked.Increment(ref _active);
		UpdatePeak(active);
		return new BudgetLease(this);
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) == 0)
			_shutdown.Cancel();
	}

	private void Return()
	{
		Interlocked.Decrement(ref _active);
		_capacity.Release();
	}

	private void UpdatePeak(int active)
	{
		while (true)
		{
			var current = Volatile.Read(ref _peakActive);
			if (current >= active ||
			    Interlocked.CompareExchange(ref _peakActive, active, current) == current)
			{
				return;
			}
		}
	}

	private sealed class BudgetLease(LanguageWorkerBudget owner) : IDisposable
	{
		private LanguageWorkerBudget? _owner = owner;

		public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Return();
	}
}

internal readonly record struct LanguageWorkerBudgetDiagnostics(
	int Capacity,
	int Active,
	int PeakActive,
	int Retained,
	int RetainedCapacity);

/// <summary>
/// One immutable language/query set shared by every parser worker. Tree-sitter explicitly permits
/// sharing TSQuery between threads; execution state remains isolated in each worker's cursors.
/// </summary>
internal sealed record LanguageRuntime(
	Language Language,
	Query Bodies,
	Query Declarations,
	Query? Preserves) : IDisposable
{
	public static LanguageRuntime Create(string libraryPath, CompressionLanguagePack pack)
	{
		var language = new Language(libraryPath, pack.Export);
		try
		{
			var bodies = new Query(language, pack.BodiesQuery);
			try
			{
				var declarations = new Query(language, pack.DeclarationsQuery);
				try
				{
					var preserves = pack.PreservesQuery is null
						? null
						: new Query(language, pack.PreservesQuery);
					try
					{
						return new LanguageRuntime(language, bodies, declarations, preserves);
					}
					catch
					{
						preserves?.Dispose();
						throw;
					}
				}
				catch
				{
					declarations.Dispose();
					throw;
				}
			}
			catch
			{
				bodies.Dispose();
				throw;
			}
		}
		catch
		{
			language.Dispose();
			throw;
		}
	}

	public void Dispose()
	{
		Preserves?.Dispose();
		Declarations.Dispose();
		Bodies.Dispose();
		Language.Dispose();
	}
}

/// <summary>One non-thread-safe parser and one reusable cursor per query.</summary>
internal sealed record LoadedLanguage(
	LanguageRuntime Runtime,
	Parser Parser,
	Tree IdleTree,
	QueryCursor BodiesCursor,
	QueryCursor DeclarationsCursor,
	QueryCursor? PreservesCursor) : IDisposable
{
	public static LoadedLanguage Create(LanguageRuntime runtime, uint queryMatchLimit)
	{
		var parser = new Parser(runtime.Language);
		try
		{
			var idleTree = parser.Parse(string.Empty) ??
			               throw new InvalidOperationException("Tree-sitter could not create an idle tree.");
			try
			{
				var bodiesCursor = CreateBoundedCursor(queryMatchLimit);
				try
				{
					var declarationsCursor = CreateBoundedCursor(queryMatchLimit);
					try
					{
						var preservesCursor = runtime.Preserves is null
							? null
							: CreateBoundedCursor(queryMatchLimit);
						try
						{
							return new LoadedLanguage(
								runtime,
								parser,
								idleTree,
								bodiesCursor,
								declarationsCursor,
								preservesCursor);
						}
						catch
						{
							preservesCursor?.Dispose();
							throw;
						}
					}
					catch
					{
						declarationsCursor.Dispose();
						throw;
					}
				}
				catch
				{
					bodiesCursor.Dispose();
					throw;
				}
			}
			catch
			{
				idleTree.Dispose();
				throw;
			}
		}
		catch
		{
			parser.Dispose();
			throw;
		}
	}

	private static QueryCursor CreateBoundedCursor(uint queryMatchLimit) =>
		new() { MatchLimit = queryMatchLimit };

	/// <summary>
	/// QueryCursor retains its last Tree and Tree retains the parsed string. Rebinding to a tiny
	/// process-lifetime tree prevents an idle pool from retaining the last large files it parsed.
	/// </summary>
	public void PrepareForReturn()
		=> ReleaseQueryTreeReferences();

	public void ReleaseQueryTreeReferences()
	{
		var root = IdleTree.RootNode;
		BodiesCursor.Execute(Runtime.Bodies, root);
		DeclarationsCursor.Execute(Runtime.Declarations, root);
		if (PreservesCursor is not null && Runtime.Preserves is not null)
			PreservesCursor.Execute(Runtime.Preserves, root);
	}

	public void Dispose()
	{
		PreservesCursor?.Dispose();
		DeclarationsCursor.Dispose();
		BodiesCursor.Dispose();
		IdleTree.Dispose();
		Parser.Dispose();
	}
}
