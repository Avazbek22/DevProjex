using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Compression;
using TreeSitter;

namespace DevProjex.Infrastructure.Dependencies;

public sealed class TreeSitterDependencyFactExtractor : IDependencyFactExtractor
{
	private const int MaximumWorkers = 8;
	private const int MaximumRetainedWorkersPerLanguage = 2;
	private const int MaximumPreparedSources = 8_192;
	private const long MaximumPreparedSourceBytes = 64L * 1024 * 1024;
	private readonly IGrammarLibraryLocator _locator;
	private readonly IReadOnlyDictionary<LanguageId, LanguageDefinition> _definitions;
	private readonly ConcurrentDictionary<LanguageId, Lazy<LanguageRuntime>> _runtimes = [];
	private readonly ConcurrentDictionary<LanguageId, string> _extractorIdentities = [];
	private readonly ConcurrentDictionary<PreparedSourceCacheKey, Lazy<Task<PreparedSourceContent>>> _preparedSources = [];
	private readonly ConcurrentDictionary<PreparedSourceCacheKey, long> _preparedSourceWeights = [];
	private readonly ConcurrentQueue<PreparedSourceCacheKey> _preparedSourceOrder = [];
	private readonly SemaphoreSlim _workerBudget = new(Math.Clamp(Environment.ProcessorCount, 1, MaximumWorkers));
	private readonly object _preparedSourceTrimSync = new();
	private long _preparedSourceBytes;
	private int _parseCount;
	private int _compiledQuerySetCount;
	private int _disposed;

	public TreeSitterDependencyFactExtractor()
		: this(CodeCompressionFactory.CreateLocator())
	{
	}

	internal TreeSitterDependencyFactExtractor(IGrammarLibraryLocator locator)
	{
		_locator = locator ?? throw new ArgumentNullException(nameof(locator));
		_definitions = LanguageDefinition.CreateAll();
	}

	public int ParseCount => Volatile.Read(ref _parseCount);
	public int CompiledQuerySetCount => Volatile.Read(ref _compiledQuerySetCount);

	public async ValueTask<PreparedDependencySource> PrepareAsync(
		string sourceRoot,
		string fullPath,
		DependencyResolverConfiguration configuration,
		CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		var relative = Normalize(Path.GetRelativePath(sourceRoot, fullPath));
		var language = ForPath(fullPath);
		var scope = configuration.Scopes
			.Where(candidate => LanguageFamily(candidate.LanguageId) == LanguageFamily(language) &&
			                    IsWithin(candidate.Root, fullPath))
			.OrderByDescending(static candidate => candidate.Root.Length)
			.Select(static candidate => candidate.ScopeId)
			.FirstOrDefault() ?? $"root:{LanguageFamily(language).ToString().ToLowerInvariant()}";
		if (language == LanguageId.Unsupported)
		{
			var info = new FileInfo(fullPath);
			var identity = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
			return new PreparedDependencySource(fullPath, relative, scope, language,
				Hash(Encoding.UTF8.GetBytes(identity)), "unsupported:v1", string.Empty,
				DependencyFileStatus.Unsupported,
				$"{Path.GetExtension(fullPath).TrimStart('.')} is not supported by the dependency engine yet");
		}

		try
		{
			var info = new FileInfo(fullPath);
			var key = new PreparedSourceCacheKey(
				Path.GetFullPath(fullPath),
				info.Length,
				info.LastWriteTimeUtc.Ticks,
				info.CreationTimeUtc.Ticks,
				language);
			var created = new Lazy<Task<PreparedSourceContent>>(
				() => ReadPreparedSourceAsync(fullPath, language, cancellationToken),
				LazyThreadSafetyMode.ExecutionAndPublication);
			var lazy = _preparedSources.GetOrAdd(key, created);
			if (ReferenceEquals(lazy, created))
				_preparedSourceOrder.Enqueue(key);
			PreparedSourceContent content;
			try
			{
				content = await lazy.Value.ConfigureAwait(false);
				if (ReferenceEquals(lazy, created))
					RegisterPreparedSourceWeight(key, lazy, EstimatePreparedSourceBytes(content));
			}
			catch
			{
				_preparedSources.TryRemove(new KeyValuePair<PreparedSourceCacheKey, Lazy<Task<PreparedSourceContent>>>(key, lazy));
				throw;
			}
			return new PreparedDependencySource(
				fullPath,
				relative,
				scope,
				language,
				content.Fingerprint,
				content.ExtractorIdentity,
				content.Source);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			return new PreparedDependencySource(fullPath, relative, scope, language,
				Hash(Encoding.UTF8.GetBytes(exception.GetType().Name)), GetExtractorIdentity(language),
				string.Empty, DependencyFileStatus.ExtractionFailed,
				$"{exception.GetType().Name}: {OneLine(exception.Message)}");
		}
	}

	private async Task<PreparedSourceContent> ReadPreparedSourceAsync(
		string fullPath,
		LanguageId language,
		CancellationToken cancellationToken)
	{
		var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
		var source = Encoding.UTF8.GetString(bytes);
		if (source.Length > 0 && source[0] == '\uFEFF') source = source[1..];
		return new PreparedSourceContent(Hash(bytes), GetExtractorIdentity(language), source);
	}

	private void RegisterPreparedSourceWeight(
		PreparedSourceCacheKey key,
		Lazy<Task<PreparedSourceContent>> entry,
		long weight)
	{
		lock (_preparedSourceTrimSync)
		{
			if (_preparedSources.TryGetValue(key, out var current) && ReferenceEquals(current, entry) &&
			    _preparedSourceWeights.TryAdd(key, weight))
				_preparedSourceBytes += weight;
			while ((_preparedSources.Count > MaximumPreparedSources ||
			        _preparedSourceBytes > MaximumPreparedSourceBytes) &&
			       _preparedSourceOrder.TryDequeue(out var oldest))
			{
				_preparedSources.TryRemove(oldest, out _);
				if (_preparedSourceWeights.TryRemove(oldest, out var removedWeight))
					_preparedSourceBytes -= removedWeight;
			}
		}
	}

	private static long EstimatePreparedSourceBytes(PreparedSourceContent content) =>
		192L + (content.Source.Length + content.Fingerprint.Length + content.ExtractorIdentity.Length) * 2L;

	private string GetExtractorIdentity(LanguageId languageId) =>
		_extractorIdentities.GetOrAdd(languageId, id =>
		{
			var definition = _definitions[id];
			var queryHash = Hash(Encoding.UTF8.GetBytes(
				ReadQuery(definition.QueryDirectory, "declarations.scm") + "\0" +
				ReadQuery(definition.QueryDirectory, "references.scm")));
			return $"{definition.Library}:TreeSitter.DotNet-1.3.0:{queryHash}";
		});

	public FileFacts Extract(PreparedDependencySource source, DependencyFactsLimits limits)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		if (source.PreparedStatus != DependencyFileStatus.Supported)
			return StatusOnly(source, source.PreparedStatus, source.PreparedStatusReason);
		if (source.Source.Length > limits.MaximumCharactersPerFile)
			return StatusOnly(source, DependencyFileStatus.ExtractionFailed,
				$"file exceeds the {limits.MaximumCharactersPerFile} character parse limit");

		try
		{
			var runtime = GetRuntime(source.LanguageId);
			using var lease = runtime.Rent(_workerBudget);
			using var tree = lease.Parser.Parse(source.Source) ??
				throw new InvalidOperationException("Tree-sitter returned no syntax tree.");
			Interlocked.Increment(ref _parseCount);
			var declarations = Capture(runtime.Declarations, tree.RootNode, limits.MaximumFactsPerFile);
			var references = Capture(runtime.References, tree.RootNode, limits.MaximumFactsPerFile);
			var errorKinds = CollectErrorKinds(tree.RootNode);
			var context = new DependencyExtractionContext(
				source.RelativePath,
				source.ScopeId,
				source.LanguageId,
				source.Source,
				source.ContentFingerprint,
				tree.RootNode.HasError,
				errorKinds,
				declarations,
				references);
			return runtime.Adapter.Extract(context, limits);
		}
		catch (Exception exception) when (exception is
		       IOException or UnauthorizedAccessException or DllNotFoundException or BadImageFormatException or
		       EntryPointNotFoundException or InvalidOperationException)
		{
			return StatusOnly(source, DependencyFileStatus.ExtractionFailed,
				$"{exception.GetType().Name}: {OneLine(exception.Message)}");
		}
	}

	private LanguageRuntime GetRuntime(LanguageId languageId)
	{
		var lazy = _runtimes.GetOrAdd(languageId, id => new Lazy<LanguageRuntime>(
			() =>
			{
				var definition = _definitions[id];
				var language = new Language(_locator.Resolve(definition.Library), definition.Export);
				try
				{
					var declarations = new Query(language, ReadQuery(definition.QueryDirectory, "declarations.scm"));
					try
					{
						var references = new Query(language, ReadQuery(definition.QueryDirectory, "references.scm"));
						Interlocked.Increment(ref _compiledQuerySetCount);
						return new LanguageRuntime(language, declarations, references, definition.Adapter);
					}
					catch
					{
						declarations.Dispose();
						throw;
					}
				}
				catch
				{
					language.Dispose();
					throw;
				}
			},
			LazyThreadSafetyMode.ExecutionAndPublication));
		try
		{
			return lazy.Value;
		}
		catch
		{
			_runtimes.TryRemove(new KeyValuePair<LanguageId, Lazy<LanguageRuntime>>(languageId, lazy));
			throw;
		}
	}

	private static IReadOnlyList<DependencySyntaxCapture> Capture(Query query, Node root, int limit)
	{
		using var cursor = query.Execute(root);
		return cursor.Captures.Take(limit + 1)
			.Select(static capture => new DependencySyntaxCapture(
				capture.Name,
				capture.Node.Type,
				capture.Node.Text,
				checked((int)capture.Node.StartPosition.Row + 1),
				checked((int)capture.Node.StartIndex),
				checked((int)capture.Node.EndIndex)))
			.OrderBy(static capture => capture.StartIndex)
			.ThenBy(static capture => capture.Name, StringComparer.Ordinal)
			.ToArray();
	}

	private static IReadOnlyDictionary<string, int> CollectErrorKinds(Node root)
	{
		if (!root.HasError) return new Dictionary<string, int>();
		var result = new Dictionary<string, int>(StringComparer.Ordinal);
		var stack = new Stack<Node>();
		stack.Push(root);
		while (stack.TryPop(out var node))
		{
			if (node.Type == "ERROR")
			{
				var kinds = node.Children.Where(static child => child.IsNamed).Select(static child => child.Type).Distinct().ToArray();
				if (kinds.Length == 0) kinds = ["<token>"];
				foreach (var kind in kinds) result[kind] = result.GetValueOrDefault(kind) + 1;
			}
			foreach (var child in node.Children) stack.Push(child);
		}
		return result;
	}

	private static FileFacts StatusOnly(PreparedDependencySource source, DependencyFileStatus status, string? reason) => new(
		source.RelativePath, source.ScopeId, source.LanguageId, source.ContentFingerprint, source.Source.Length,
		status, reason, false, new Dictionary<string, int>(), [], [], [], [], new Dictionary<string, string>(), [], new Dictionary<string, string>(), []);

	private static string ReadQuery(string directory, string file)
	{
		var name = $"DevProjex.Infrastructure.Dependencies.Languages.{directory}.{file}";
		using var stream = typeof(TreeSitterDependencyFactExtractor).Assembly.GetManifestResourceStream(name) ??
			throw new InvalidOperationException($"Dependency query resource '{name}' is missing.");
		using var reader = new StreamReader(stream, Encoding.UTF8);
		return reader.ReadToEnd();
	}

	private static LanguageId ForPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
	{
		".cs" => LanguageId.CSharp,
		".ts" or ".mts" or ".cts" => LanguageId.TypeScript,
		".tsx" or ".jsx" => LanguageId.Tsx,
		".js" or ".mjs" or ".cjs" => LanguageId.JavaScript,
		".py" or ".pyi" => LanguageId.Python,
		_ => LanguageId.Unsupported
	};

	private static LanguageId LanguageFamily(LanguageId language) => language is LanguageId.JavaScript or LanguageId.Tsx
		? LanguageId.TypeScript
		: language;
	private static string Normalize(string path) => path.Replace('\\', '/');
	private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
	private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
	private static bool IsWithin(string root, string path)
	{
		var relative = Path.GetRelativePath(root, path);
		return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		_preparedSources.Clear();
		_preparedSourceWeights.Clear();
		foreach (var runtime in _runtimes.Values.Where(static value => value.IsValueCreated))
			runtime.Value.Dispose();
		_workerBudget.Dispose();
		(_locator as IDisposable)?.Dispose();
	}

	private readonly record struct PreparedSourceCacheKey(
		string Path,
		long Length,
		long LastWriteTimeUtcTicks,
		long CreationTimeUtcTicks,
		LanguageId LanguageId);

	private sealed record PreparedSourceContent(
		string Fingerprint,
		string ExtractorIdentity,
		string Source);

	private sealed record LanguageDefinition(
		string Library,
		string Export,
		string QueryDirectory,
		IDependencyLanguageAdapter Adapter)
	{
		public static IReadOnlyDictionary<LanguageId, LanguageDefinition> CreateAll() =>
			new Dictionary<LanguageId, LanguageDefinition>
			{
				[LanguageId.CSharp] = new("tree-sitter-c-sharp", "tree_sitter_c_sharp", "csharp", new CSharpDependencyLanguageAdapter()),
				[LanguageId.TypeScript] = new("tree-sitter-typescript", "tree_sitter_typescript", "typescript", new TypeScriptDependencyLanguageAdapter()),
				[LanguageId.Tsx] = new("tree-sitter-tsx", "tree_sitter_tsx", "typescript", new TypeScriptDependencyLanguageAdapter()),
				[LanguageId.JavaScript] = new("tree-sitter-javascript", "tree_sitter_javascript", "javascript", new TypeScriptDependencyLanguageAdapter()),
				[LanguageId.Python] = new("tree-sitter-python", "tree_sitter_python", "python", new PythonDependencyLanguageAdapter())
			};
	}

	private sealed class LanguageRuntime(
		Language language,
		Query declarations,
		Query references,
		IDependencyLanguageAdapter adapter) : IDisposable
	{
		private readonly ConcurrentBag<Parser> _parsers = [];
		private int _retained;
		public Query Declarations { get; } = declarations;
		public Query References { get; } = references;
		public IDependencyLanguageAdapter Adapter { get; } = adapter;

		public ParserLease Rent(SemaphoreSlim budget)
		{
			budget.Wait();
			if (_parsers.TryTake(out var parser))
			{
				Interlocked.Decrement(ref _retained);
				return new ParserLease(this, parser, budget);
			}
			return new ParserLease(this, new Parser(language), budget);
		}

		public void Return(Parser parser, SemaphoreSlim budget)
		{
			if (Interlocked.Increment(ref _retained) <= MaximumRetainedWorkersPerLanguage)
				_parsers.Add(parser);
			else
			{
				Interlocked.Decrement(ref _retained);
				parser.Dispose();
			}
			budget.Release();
		}

		public void Dispose()
		{
			while (_parsers.TryTake(out var parser)) parser.Dispose();
			References.Dispose();
			Declarations.Dispose();
			language.Dispose();
		}
	}

	private sealed class ParserLease(LanguageRuntime owner, Parser parser, SemaphoreSlim budget) : IDisposable
	{
		private LanguageRuntime? _owner = owner;
		public Parser Parser { get; } = parser;
		public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Return(Parser, budget);
	}
}
