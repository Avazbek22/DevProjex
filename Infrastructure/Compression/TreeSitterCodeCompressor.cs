using DevProjex.Application.Compression;
using System.Collections.Concurrent;
using TreeSitter;

namespace DevProjex.Infrastructure.Compression;

/// <summary>One language this build can compress, as seen from outside Infrastructure.</summary>
public sealed record CompressionLanguageInfo(
	string Id,
	string DisplayName,
	IReadOnlyList<string> Extensions,
	string GrammarLibrary);

/// <summary>
/// Builds compression plans with tree-sitter. Everything language-specific is data: the grammar,
/// the queries and the placeholder come from <see cref="CompressionLanguagePack"/>.
/// </summary>
public sealed class TreeSitterCodeCompressor : ICodeCompressor, IDisposable
{
	/// <summary>
	/// Parsing cannot be interrupted. tree-sitter 0.26.3 removed ts_parser_set_timeout_micros and
	/// ts_parser_set_cancellation_flag, and the binding does not expose the replacement
	/// ts_parser_parse_with_options, so a pathological file can neither be bounded nor aborted once
	/// started. A size cap before parsing is the only defence there is; files above it are reported
	/// as UnchangedTooLarge so the user sees a reason rather than an unexplained refusal.
	/// </summary>
	public const int MaximumParsableCharacters = 2 * 1024 * 1024;

	private readonly IGrammarLibraryLocator _locator;
	private readonly IReadOnlyList<CompressionLanguagePack> _packs;
	private readonly Dictionary<string, CompressionLanguagePack> _byExtension;
	private readonly ConcurrentDictionary<string, Lazy<LanguageWorkerPool>> _languagePools = [];
	private bool _disposed;

	public TreeSitterCodeCompressor(IGrammarLibraryLocator locator)
		: this(locator, CompressionLanguagePack.LoadAll())
	{
	}

	internal TreeSitterCodeCompressor(IGrammarLibraryLocator locator, IReadOnlyList<CompressionLanguagePack> packs)
	{
		_locator = locator;
		_packs = packs;
		_byExtension = packs
			.SelectMany(pack => pack.Extensions.Select(extension => (extension, pack)))
			.ToDictionary(pair => pair.extension, pair => pair.pack, StringComparer.OrdinalIgnoreCase);
		TransformIdentity = "tree-sitter:" + string.Join(",", packs.Select(static pack => pack.Identity));
	}

	public string TransformIdentity { get; }

	internal IReadOnlyList<CompressionLanguagePack> Packs => _packs;

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
			pack.Library)).ToArray();

	public bool IsSupported(string relativePath) =>
		_byExtension.ContainsKey(Path.GetExtension(relativePath));

	public ICodeCompressionScope CreateScope(string projectRoot)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		return new TreeSitterCompressionScope(_byExtension, TransformIdentity, GetLanguagePool);
	}

	private LanguageWorkerPool GetLanguagePool(CompressionLanguagePack pack) =>
		_languagePools.GetOrAdd(
			pack.Id,
			_ => new Lazy<LanguageWorkerPool>(
				() => new LanguageWorkerPool(_locator, pack),
				LazyThreadSafetyMode.ExecutionAndPublication)).Value;

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		foreach (var pool in _languagePools.Values)
		{
			if (pool.IsValueCreated)
				pool.Value.Dispose();
		}
		_languagePools.Clear();
	}
}

/// <summary>
/// One output operation. It borrows process-lifetime parser workers lazily, only for languages that
/// actually appear; the scope itself owns no native parser and is safe for parallel file analysis.
/// </summary>
internal sealed class TreeSitterCompressionScope(
	IReadOnlyDictionary<string, CompressionLanguagePack> byExtension,
	string transformIdentity,
	Func<CompressionLanguagePack, LanguageWorkerPool> languagePoolProvider) : ICodeCompressionScope
{
	private bool _disposed;

	public CodeCompressionAnalysis Analyze(
		string fullPath,
		string relativePath,
		string content,
		CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);

		if (!byExtension.TryGetValue(Path.GetExtension(relativePath), out var pack))
			return Refused(relativePath, "unknown", CodeCompressionOutcome.UnchangedUnsupportedLanguage, content.Length);

		if (content.Length > TreeSitterCodeCompressor.MaximumParsableCharacters)
			return Refused(relativePath, pack.Id, CodeCompressionOutcome.UnchangedTooLarge, content.Length);

		cancellationToken.ThrowIfCancellationRequested();

		LanguageWorkerLease lease;
		try
		{
			lease = languagePoolProvider(pack).Rent(cancellationToken);
		}
		catch (Exception exception) when (exception is DllNotFoundException or InvalidOperationException or IOException)
		{
			return Refused(relativePath, pack.Id, CodeCompressionOutcome.UnchangedParseFailed, content.Length);
		}

		using var languageLease = lease;
		var language = languageLease.Worker;
		var source = content;
		using var original = language.Parser.Parse(source);
		if (original is null)
			return Refused(relativePath, pack.Id, CodeCompressionOutcome.UnchangedParseFailed, content.Length);

		var edits = CollectEdits(pack, language, original, source, cancellationToken);
		CodeCompressionPlan plan;
		try
		{
			plan = CodeCompressionPlan.Create(relativePath, pack.Id, edits, source.Length, transformIdentity);
		}
		catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
		{
			// A query that captures overlapping or out-of-range spans is a defect in the language
			// pack, but Plan is contracted never to throw for a refusal: a malformed pattern must
			// leave one file uncompressed, not take down the export.
			return Refused(relativePath, pack.Id, CodeCompressionOutcome.UnchangedGateRejected, source.Length);
		}

		if (plan.Outcome != CodeCompressionOutcome.Compressed)
			return new CodeCompressionAnalysis(plan, null);

		var applied = plan.Apply(source);
		using var compressed = language.Parser.Parse(applied.Text);
		if (compressed is null)
			return new CodeCompressionAnalysis(
				plan.ToUnchanged(CodeCompressionOutcome.UnchangedGateRejected),
				null);

		var verdict = CodeStructureGate.Evaluate(
			ReadDeclarations(language, original.RootNode, ContentTransformMap.Identity),
			ReadDeclarations(language, compressed.RootNode, applied.Map),
			ReadDefects(original.RootNode, ContentTransformMap.Identity),
			ReadDefects(compressed.RootNode, applied.Map),
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

	private static List<CodeCompressionEdit> CollectEdits(
		CompressionLanguagePack pack,
		LoadedLanguage language,
		Tree tree,
		string source,
		CancellationToken cancellationToken)
	{
		// Python keeps documentation inside the body, so the first string of a suite is preserved
		// and only what follows it is removed. Every other language documents above the declaration.
		var docstringEnds = new Dictionary<int, int>();
		if (language.Docstrings is not null)
		{
			foreach (var capture in language.Docstrings.Execute(tree.RootNode).Captures)
			{
				var block = capture.Node.Parent;
				if (block is not null)
					docstringEnds[block.StartIndex] = capture.Node.EndIndex;
			}
		}

		var raw = new List<(int Start, int End, string Type)>();
		foreach (var capture in language.Bodies.Execute(tree.RootNode).Captures)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var node = capture.Node;

			// Defence in depth. The queries are anchored on the parent declaration's body: field, so
			// a container should be unreachable - but a grammar upgrade must fail loudly, not
			// silently delete a class body.
			if (pack.ContainerNodeTypes.Contains(node.Type))
				continue;

			int start;
			if (docstringEnds.TryGetValue(node.StartIndex, out var docEnd))
			{
				start = docEnd;
			}
			else
			{
				// A comment on the first line of a body is lexically inside the function but is an
				// "extra" node that sits BEFORE the body node, so the query never reaches it.
				// Leaving it behind would print a comment describing code that is gone.
				start = ExtendOverLeadingComments(source, node);
			}

			if (start < 0 || node.EndIndex > source.Length || node.EndIndex <= start)
				continue;
			raw.Add((start, node.EndIndex, node.Type));
		}

		// Outermost wins: a lambda body inside a method body must not be spliced twice.
		raw.Sort((left, right) => left.Start != right.Start ? left.Start.CompareTo(right.Start) : right.End.CompareTo(left.End));
		var edits = new List<CodeCompressionEdit>(raw.Count);
		var reach = -1;
		foreach (var (start, end, type) in raw)
		{
			if (start < reach)
				continue;
			reach = end;
			edits.Add(new CodeCompressionEdit(start, end - start, PlaceholderFor(pack, source, start, end, type)));
		}

		return edits;
	}

	/// <summary>
	/// Walks the edit start back over whole comment-only lines that sit between the declaration and
	/// its body. Only complete lines are taken, and never past the declaration itself, so nothing
	/// the query did not intend to remove can be swallowed.
	/// </summary>
	private static int ExtendOverLeadingComments(string source, Node body)
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
			if (line.Length == 0 || !(line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal)))
				return start;

			start = previousLineStart + 1;
		}
	}

	/// <summary>
	/// A placeholder must be valid syntax: the reverse-parse gate refuses anything that does not
	/// parse. Empty blocks are deliberately neutral and avoid exposing implementation-style comment
	/// markers in the generated context. Python uses its valid ellipsis statement instead.
	/// A body that only kept its docstring already ends where the placeholder would go, so it needs
	/// nothing appended.
	/// </summary>
	private static string PlaceholderFor(CompressionLanguagePack pack, string source, int start, int end, string nodeType)
	{
		// An expression body is not a block: replacing "=> value" with "{ ... }" leaves the
		// declaration's trailing semicolon after a block and the file stops parsing. The gate
		// caught exactly this, which is what it is for.
		if (nodeType.Equals("arrow_expression_clause", StringComparison.Ordinal))
			return pack.ExpressionPlaceholder ?? pack.BlockPlaceholder;

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

		var trailingNewline = end > 0 && end <= source.Length && source[end - 1] == '\n' ? "\n" : string.Empty;

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
				: $"\n{indentation}";
		return $"{leading}{pack.BlockPlaceholder}{trailingNewline}";
	}

	private static List<CodeDeclaration> ReadDeclarations(LoadedLanguage language, Node root, ContentTransformMap map)
	{
		var declarations = new List<CodeDeclaration>();
		foreach (var match in language.Declarations.Execute(root).Matches)
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
			if (!map.TryToSource(declaration.StartIndex, out var start))
				continue;
			if (!map.TryToSource(declaration.EndIndex, out var end))
				end = start;
			declarations.Add(new CodeDeclaration(declaration.Type, name ?? string.Empty, start, Math.Max(0, end - start)));
		}

		declarations.Sort(static (left, right) =>
			left.Start != right.Start ? left.Start.CompareTo(right.Start) : string.CompareOrdinal(left.Kind, right.Kind));
		return declarations;
	}

	private static List<CodeParseDefect> ReadDefects(Node root, ContentTransformMap map)
	{
		var defects = new List<CodeParseDefect>();
		var stack = new Stack<Node>();
		stack.Push(root);
		while (stack.Count > 0)
		{
			var node = stack.Pop();
			// See CodeParseDefect: HasError lies in both directions against the shipped grammars,
			// and a MISSING node surfaces here only as a named node of zero width.
			var isDefect = node.IsError || node.IsMissing || (node.IsNamed && node.StartIndex == node.EndIndex);
			if (isDefect)
			{
				var kind = node.IsError ? "ERROR" : "MISSING";
				defects.Add(new CodeParseDefect(kind, map.TryToSource(node.StartIndex, out var source) ? source : -1));
			}

			foreach (var child in node.Children)
				stack.Push(child);
		}

		return defects;
	}

	public void Dispose() => _disposed = true;
}

/// <summary>
/// Bounded process-lifetime parser pool. Grammar verification and materialization happen once;
/// native parser state is never entered concurrently.
/// </summary>
internal sealed class LanguageWorkerPool : IDisposable
{
	private const int MaximumWorkers = 8;
	private readonly CompressionLanguagePack _pack;
	private readonly Lazy<string> _libraryPath;
	private readonly ConcurrentBag<LoadedLanguage> _available = [];
	private readonly SemaphoreSlim _capacity;
	private readonly object _sync = new();
	private bool _disposed;

	public LanguageWorkerPool(IGrammarLibraryLocator locator, CompressionLanguagePack pack)
	{
		_pack = pack;
		_libraryPath = new Lazy<string>(
			() => locator.Resolve(pack.Library),
			LazyThreadSafetyMode.ExecutionAndPublication);
		var workerCount = Math.Clamp(Environment.ProcessorCount, 1, MaximumWorkers);
		_capacity = new SemaphoreSlim(workerCount, workerCount);
	}

	public LanguageWorkerLease Rent(CancellationToken cancellationToken)
	{
		_capacity.Wait(cancellationToken);
		LoadedLanguage? worker = null;
		try
		{
			lock (_sync)
			{
				ObjectDisposedException.ThrowIf(_disposed, this);
				_available.TryTake(out worker);
			}

			worker ??= LoadedLanguage.Create(_libraryPath.Value, _pack);
			lock (_sync)
				ObjectDisposedException.ThrowIf(_disposed, this);
			return new LanguageWorkerLease(this, worker);
		}
		catch
		{
			worker?.Dispose();
			ReleaseCapacity();
			throw;
		}
	}

	internal void Return(LoadedLanguage worker)
	{
		var dispose = false;
		lock (_sync)
		{
			if (_disposed)
				dispose = true;
			else
				_available.Add(worker);
		}

		if (dispose)
			worker.Dispose();
		else
			ReleaseCapacity();
	}

	public void Dispose()
	{
		lock (_sync)
		{
			if (_disposed)
				return;
			_disposed = true;
			while (_available.TryTake(out var worker))
				worker.Dispose();
		}
		_capacity.Dispose();
	}

	private void ReleaseCapacity()
	{
		try
		{
			_capacity.Release();
		}
		catch (ObjectDisposedException)
		{
			// Disposal stops new rents. A worker already leased is disposed by Return instead.
		}
	}
}

internal sealed class LanguageWorkerLease(
	LanguageWorkerPool owner,
	LoadedLanguage worker) : IDisposable
{
	private LanguageWorkerPool? _owner = owner;

	public LoadedLanguage Worker { get; } = worker;

	public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Return(Worker);
}

/// <summary>One non-thread-safe native parser and its compiled queries.</summary>
internal sealed record LoadedLanguage(
	Language Language,
	Parser Parser,
	Query Bodies,
	Query Declarations,
	Query? Docstrings) : IDisposable
{
	public static LoadedLanguage Create(string libraryPath, CompressionLanguagePack pack)
	{
		var language = new Language(libraryPath, pack.Export);
		try
		{
			var parser = new Parser(language);
			try
			{
				var bodies = new Query(language, pack.BodiesQuery);
				try
				{
					var declarations = new Query(language, pack.DeclarationsQuery);
					try
					{
						var docstrings = pack.DocstringsQuery is null
							? null
							: new Query(language, pack.DocstringsQuery);
						return new LoadedLanguage(language, parser, bodies, declarations, docstrings);
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
				parser.Dispose();
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
		Docstrings?.Dispose();
		Declarations.Dispose();
		Bodies.Dispose();
		Parser.Dispose();
		Language.Dispose();
	}
}
