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
	private const string DiagnosticErrorQuery = "(ERROR) @diagnostic.error";
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
				ReadQuery(definition.QueryDirectory, "references.scm") + "\0" + DiagnosticErrorQuery));
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
			var (declarations, references, errorKinds) = CaptureFacts(
				runtime.Facts,
				tree.RootNode,
				limits.MaximumFactsPerFile);
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
					var facts = new Query(language,
						ReadQuery(definition.QueryDirectory, "declarations.scm") + "\n" +
						ReadQuery(definition.QueryDirectory, "references.scm") + "\n" +
						DiagnosticErrorQuery);
					Interlocked.Increment(ref _compiledQuerySetCount);
					return new LanguageRuntime(language, facts, definition.Adapter);
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

	private static (
		IReadOnlyList<DependencySyntaxCapture> Declarations,
		IReadOnlyList<DependencySyntaxCapture> References,
		IReadOnlyDictionary<string, int> ErrorKinds) CaptureFacts(Query query, Node root, int limit)
	{
		using var cursor = query.Execute(root);
		var declarations = new List<DependencySyntaxCapture>();
		var references = new List<DependencySyntaxCapture>();
		var errorKinds = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (var capture in cursor.Captures)
		{
			if (capture.Name == "diagnostic.error")
			{
				var kinds = capture.Node.Children.Where(static child => child.IsNamed)
					.Select(static child => child.Type).Distinct().ToArray();
				if (kinds.Length == 0) kinds = ["<token>"];
				foreach (var kind in kinds)
					errorKinds[kind] = errorKinds.GetValueOrDefault(kind) + 1;
				continue;
			}
			var target = IsDeclarationCapture(capture.Name) ? declarations : references;
			if (target.Count <= limit)
				target.Add(CreateCapture(capture.Name, capture.Node));
		}
		return (
			declarations.OrderBy(static capture => capture.StartIndex)
				.ThenBy(static capture => capture.Name, StringComparer.Ordinal).ToArray(),
			references.OrderBy(static capture => capture.StartIndex)
				.ThenBy(static capture => capture.Name, StringComparer.Ordinal).ToArray(),
			errorKinds);
	}

	private static bool IsDeclarationCapture(string captureName) =>
		captureName.StartsWith("declaration.", StringComparison.Ordinal) ||
		captureName is "context.namespace" or "context.using";

	private static DependencySyntaxCapture CreateCapture(string captureName, Node node)
	{
		var isCompact = captureName.StartsWith("declaration.", StringComparison.Ordinal) ||
			captureName == "context.namespace";
		if (!isCompact)
			return CreateCapture(captureName, node, node.Text, null, 0, false);

		var capturedName = node.GetChildForField("name")?.Text;
		var typeParameters = node.Children.FirstOrDefault(static child =>
			child.Type is "type_parameter_list" or "type_parameters");
		var genericArity = typeParameters is null ? 0 : CountGenericArity(typeParameters.Text);
		var evidence = string.IsNullOrEmpty(capturedName)
			? string.Empty
			: capturedName + (genericArity == 0 ? string.Empty : $"`{genericArity}");
		var isFileLocal = captureName.StartsWith("declaration.", StringComparison.Ordinal) &&
			node.Children.Any(static child => child.Type == "modifier" && child.Text == "file");
		return CreateCapture(captureName, node, evidence, capturedName, genericArity, isFileLocal);
	}

	private static DependencySyntaxCapture CreateCapture(
		string captureName,
		Node node,
		string text,
		string? capturedName,
		int genericArity,
		bool isFileLocal) =>
		new(
			captureName,
			node.Type,
			text,
			checked((int)node.StartPosition.Row + 1),
			checked((int)node.StartIndex),
			checked((int)node.EndIndex),
			capturedName,
			genericArity,
			isFileLocal);

	private static int CountGenericArity(string text)
	{
		var depth = 0;
		var arity = 1;
		foreach (var character in text)
		{
			switch (character)
			{
				case '<':
					depth++;
					break;
				case '>' when --depth == 0:
					return arity;
				case ',' when depth == 1:
					arity++;
					break;
			}
		}
		return 0;
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
		Query facts,
		IDependencyLanguageAdapter adapter) : IDisposable
	{
		private readonly ConcurrentBag<Parser> _parsers = [];
		private int _retained;
		public Query Facts { get; } = facts;
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
			Facts.Dispose();
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
