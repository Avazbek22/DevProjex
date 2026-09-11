using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DevProjex.Application.Dependencies;
using DevProjex.Application.Services;
using DevProjex.Infrastructure.Compression;
using TreeSitter;

namespace DevProjex.Infrastructure.Dependencies;

public sealed class TreeSitterDependencyFactExtractor : IDependencyFactExtractor, IDependencyNavigationExtractor
{
	private const int MaximumWorkers = 8;
	private const int MaximumRetainedWorkersPerLanguage = 2;
	private const int MaximumPreparedSources = 8_192;
	private const long MaximumPreparedSourceBytes = 64L * 1024 * 1024;
	private const string DiagnosticErrorQuery = "(ERROR) @diagnostic.error";
	private readonly IGrammarLibraryLocator _locator;
	private static readonly ConditionalWeakTable<DependencyResolverConfiguration, ScopeOwnerIndex> ScopeOwners = new();
	private readonly IFileContentAnalyzer _contentAnalyzer;
	private readonly BoundedDependencySourceReader? _boundedSourceReader;
	private readonly IReadOnlyDictionary<LanguageId, LanguageDefinition> _definitions;
	private readonly ConcurrentDictionary<LanguageId, Lazy<LanguageRuntime>> _runtimes = [];
	private readonly ConcurrentDictionary<LanguageId, string> _extractorIdentities = [];
	private readonly ConcurrentDictionary<PreparedSourceCacheKey, PreparedSourceCacheEntry> _preparedSources = [];
	private readonly LinkedList<PreparedSourceCacheEntry> _preparedSourceOrder = [];
	private readonly SemaphoreSlim _workerBudget = new(Math.Clamp(Environment.ProcessorCount, 1, MaximumWorkers));
	private readonly object _preparedSourceTrimSync = new();
	private long _preparedSourceBytes;
	private long _preparedSourceGeneration;
	private int _parseCount;
	private int _compiledQuerySetCount;
	private long _rawCapturesVisited;
	private long _createdCaptures;
	private long _materializedCharacters;
	private long _adapterVisitedRanges;
	private long _adapterComparisons;
	private long _createdFacts;
	private int _disposed;

	public TreeSitterDependencyFactExtractor()
		: this(CodeCompressionFactory.CreateLocator())
	{
	}

	public TreeSitterDependencyFactExtractor(FileContentReadStreamOpener sourceOpener)
		: this(
			CodeCompressionFactory.CreateLocator(),
			new FileContentAnalyzer(sourceOpener ?? throw new ArgumentNullException(nameof(sourceOpener))),
			new BoundedDependencySourceReader(sourceOpener))
	{
	}

	internal TreeSitterDependencyFactExtractor(IGrammarLibraryLocator locator)
		: this(locator, new FileContentAnalyzer(), new BoundedDependencySourceReader())
	{
	}

	internal TreeSitterDependencyFactExtractor(
		IGrammarLibraryLocator locator,
		IFileContentAnalyzer contentAnalyzer)
		: this(locator, contentAnalyzer, null)
	{
	}

	internal TreeSitterDependencyFactExtractor(
		IGrammarLibraryLocator locator,
		IFileContentAnalyzer contentAnalyzer,
		BoundedDependencySourceReader? boundedSourceReader)
	{
		_locator = locator ?? throw new ArgumentNullException(nameof(locator));
		_contentAnalyzer = contentAnalyzer ?? throw new ArgumentNullException(nameof(contentAnalyzer));
		_boundedSourceReader = boundedSourceReader;
		_definitions = LanguageDefinition.CreateAll();
	}

	public int ParseCount => Volatile.Read(ref _parseCount);
	public int CompiledQuerySetCount => Volatile.Read(ref _compiledQuerySetCount);
	internal DependencyExtractionWorkState WorkState => new(
		Interlocked.Read(ref _rawCapturesVisited),
		Interlocked.Read(ref _createdCaptures),
		Interlocked.Read(ref _materializedCharacters),
		Interlocked.Read(ref _adapterVisitedRanges),
		Interlocked.Read(ref _adapterComparisons),
		Interlocked.Read(ref _createdFacts),
		ParseCount);
	internal PreparedSourceCacheState CacheState
	{
		get
		{
			lock (_preparedSourceTrimSync)
				return new(_preparedSources.Count, _preparedSourceOrder.Count, _preparedSourceBytes);
		}
	}

	public async ValueTask<PreparedDependencySource> PrepareAsync(
		string sourceRoot,
		string fullPath,
		DependencyResolverConfiguration configuration,
		DependencyFactsLimits limits,
		CancellationToken cancellationToken,
		string? contentIdentity = null)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		var relative = Normalize(Path.GetRelativePath(sourceRoot, fullPath));
		var language = ForPath(fullPath);
		var scope = ScopeOwners.GetValue(configuration, static value => new ScopeOwnerIndex(value.Scopes))
			.Resolve(language, fullPath);
		try
		{
			if (language == LanguageId.Unsupported)
			{
				var unsupportedInfo = new FileInfo(fullPath);
				var identity = $"{unsupportedInfo.Length}:{unsupportedInfo.LastWriteTimeUtc.Ticks}";
				return new PreparedDependencySource(fullPath, relative, scope, language,
					Hash(Encoding.UTF8.GetBytes(identity)), "unsupported:v1", string.Empty,
					DependencyFileStatus.Unsupported,
					"file language is not supported by the dependency engine yet");
			}

			var info = new FileInfo(fullPath);
			var key = new PreparedSourceCacheKey(
				Path.GetFullPath(fullPath),
				info.Length,
				info.LastWriteTimeUtc.Ticks,
				info.CreationTimeUtc.Ticks,
				language,
				limits.MaximumCharactersPerFile);
			var (entry, ownsEntry) = GetOrCreatePreparedSource(
				key,
				contentIdentity,
				fullPath,
				language,
				limits.MaximumCharactersPerFile,
				cancellationToken);
			PreparedSourceContent content;
			try
			{
				content = await entry.Value.Value.ConfigureAwait(false);
				if (!content.CanCache)
					RemovePreparedSource(key, entry);
				else if (ownsEntry)
					RegisterPreparedSourceWeight(key, entry, EstimatePreparedSourceBytes(content));
			}
			catch
			{
				RemovePreparedSource(key, entry);
				throw;
			}
			// Only a call that performed the read may claim to have observed the file. A reused
			// preparation is reported as reuse, so a caller can tell an observation apart from an
			// echo of the identity it supplied itself.
			var observation = !ownsEntry
				? new DependencySourceObservation(DependencySourceObservationKind.ReusedPreparation)
				: content.ObservedContentDigest is { } observedDigest
					? new DependencySourceObservation(DependencySourceObservationKind.Read, observedDigest)
					: default;
			return new PreparedDependencySource(
				fullPath,
				relative,
				scope,
				language,
				content.Fingerprint,
				content.ExtractorIdentity,
				content.Source,
				content.Status,
				content.StatusReason,
				content.CanCache,
				observation);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			return new PreparedDependencySource(fullPath, relative, scope, language,
				Hash(Encoding.UTF8.GetBytes(exception.GetType().Name)),
				language == LanguageId.Unsupported ? "unsupported:v1" : GetExtractorIdentity(language),
				string.Empty, DependencyFileStatus.ExtractionFailed,
				"source file could not be read",
				CanCache: false);
		}
	}

	private void RemovePreparedSource(PreparedSourceCacheKey key, PreparedSourceCacheEntry entry)
	{
		lock (_preparedSourceTrimSync)
			RemovePreparedSourceUnderLock(key, entry);
	}

	private (PreparedSourceCacheEntry Entry, bool OwnsEntry) GetOrCreatePreparedSource(
		PreparedSourceCacheKey key,
		string? requestedIdentity,
		string fullPath,
		LanguageId language,
		int maximumCharacters,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock (_preparedSourceTrimSync)
		{
			if (_preparedSources.TryGetValue(key, out var entry) &&
			    (requestedIdentity is null ||
			    entry.ContentIdentity is not null &&
			    string.Equals(entry.ContentIdentity, requestedIdentity, StringComparison.Ordinal)))
				return (entry, false);
			if (entry is not null)
				RemovePreparedSourceUnderLock(key, entry);
			var created = new PreparedSourceCacheEntry(
				key,
				Interlocked.Increment(ref _preparedSourceGeneration),
				requestedIdentity,
				new Lazy<Task<PreparedSourceContent>>(
					() => ReadPreparedSourceAsync(fullPath, language, maximumCharacters, cancellationToken),
					LazyThreadSafetyMode.ExecutionAndPublication));
			AddPreparedSourceUnderLock(key, created);
			return (created, true);
		}
	}

	private void AddPreparedSourceUnderLock(
		PreparedSourceCacheKey key,
		PreparedSourceCacheEntry entry)
	{
		_preparedSources[key] = entry;
		entry.OrderNode = _preparedSourceOrder.AddLast(entry);
		TrimPreparedSourcesUnderLock();
	}

	private bool RemovePreparedSourceUnderLock(
		PreparedSourceCacheKey key,
		PreparedSourceCacheEntry entry)
	{
		if (!_preparedSources.TryRemove(
			    new KeyValuePair<PreparedSourceCacheKey, PreparedSourceCacheEntry>(key, entry)))
			return false;
		if (entry.OrderNode is not null)
		{
			_preparedSourceOrder.Remove(entry.OrderNode);
			entry.OrderNode = null;
		}
		ReleasePreparedSourceWeightUnderLock(entry);
		return true;
	}

	private async Task<PreparedSourceContent> ReadPreparedSourceAsync(
		string fullPath,
		LanguageId language,
		int maximumCharacters,
		CancellationToken cancellationToken)
	{
		if (_boundedSourceReader is not null)
		{
			var read = await _boundedSourceReader
				.ReadAsync(fullPath, maximumCharacters, cancellationToken)
				.ConfigureAwait(false);
			return new PreparedSourceContent(
				read.Fingerprint,
				GetExtractorIdentity(language),
				read.Source,
				read.Status,
				read.StatusReason,
				read.CanCache,
				read.ObservedContentDigest);
		}
		await using var snapshot = await _contentAnalyzer
			.OpenCompleteSnapshotAsync(fullPath, cancellationToken)
			.ConfigureAwait(false);
		var result = snapshot.Result;
		var extractorIdentity = GetExtractorIdentity(language);
		if (result.Classification != FileContentClassification.Text || result.Metrics is null)
		{
			var canCache = result.Classification is not (
				FileContentClassification.AccessDenied or
				FileContentClassification.Missing or
				FileContentClassification.Unreadable);
			return new PreparedSourceContent(
				FingerprintForUnavailableFile(fullPath, result.Classification),
				extractorIdentity,
				string.Empty,
				DependencyFileStatus.ExtractionFailed,
				$"source is {result.Classification.ToString().ToLowerInvariant()}",
				canCache);
		}
		if (result.Metrics.CharCount > maximumCharacters)
		{
			return new PreparedSourceContent(
				FingerprintForUnavailableFile(fullPath, result.Classification),
				extractorIdentity,
				string.Empty,
				DependencyFileStatus.ExtractionFailed,
				$"file exceeds the {maximumCharacters} character parse limit");
		}

		if (result.Metrics.CharCount == 0)
			return new PreparedSourceContent(Hash([]), extractorIdentity, string.Empty);

		var rented = ArrayPool<char>.Shared.Rent(result.Metrics.CharCount);
		var written = 0;
		try
		{
			await snapshot.CopyTextToAsync(
				result.Metrics.CharCount,
				(chunk, token) =>
				{
					token.ThrowIfCancellationRequested();
					if (written > maximumCharacters - chunk.Length)
						throw new IOException("The source exceeded its character limit while being decoded.");
					chunk.Span.CopyTo(rented.AsSpan(written));
					written += chunk.Length;
					return ValueTask.CompletedTask;
				},
				cancellationToken).ConfigureAwait(false);
			var source = new string(rented, 0, written);
			return new PreparedSourceContent(
				ContentFingerprint.Compute(source.AsSpan()).ToHexString().ToLowerInvariant(),
				extractorIdentity,
				source);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(rented.AsSpan(0, written)));
			ArrayPool<char>.Shared.Return(rented);
		}
	}

	private static string FingerprintForUnavailableFile(
		string fullPath,
		FileContentClassification classification)
	{
		var info = new FileInfo(fullPath);
		return Hash(Encoding.UTF8.GetBytes(
			$"{classification}:{info.Length}:{info.LastWriteTimeUtc.Ticks}:{info.CreationTimeUtc.Ticks}"));
	}

	private void RegisterPreparedSourceWeight(
		PreparedSourceCacheKey key,
		PreparedSourceCacheEntry entry,
		long weight)
	{
		lock (_preparedSourceTrimSync)
		{
			if (_preparedSources.TryGetValue(key, out var current) && ReferenceEquals(current, entry) &&
			    entry.RegisteredWeight == 0)
			{
				entry.RegisteredWeight = weight;
				_preparedSourceBytes += weight;
			}
			TrimPreparedSourcesUnderLock();
		}
	}

	private void TrimPreparedSourcesUnderLock()
	{
		while ((_preparedSources.Count > MaximumPreparedSources ||
		        _preparedSourceBytes > MaximumPreparedSourceBytes) &&
		       _preparedSourceOrder.First is { Value: var oldest })
			RemovePreparedSourceUnderLock(oldest.Key, oldest);
	}

	private void ReleasePreparedSourceWeightUnderLock(PreparedSourceCacheEntry entry)
	{
		if (entry.RegisteredWeight == 0)
			return;
		_preparedSourceBytes -= entry.RegisteredWeight;
		entry.RegisteredWeight = 0;
	}

	private static long EstimatePreparedSourceBytes(PreparedSourceContent content) =>
		192L + (content.Source.Length + content.Fingerprint.Length + content.ExtractorIdentity.Length) * 2L;

	private string GetExtractorIdentity(LanguageId languageId) =>
		_extractorIdentities.GetOrAdd(languageId, id =>
		{
			var definition = _definitions[id];
			var queryHash = Hash(Encoding.UTF8.GetBytes(
				ReadQuery(definition.QueryDirectory, "declarations.scm") + "\0" +
				ReadQuery(definition.QueryDirectory, "references.scm") + "\0" +
				ReadQuery(definition.QueryDirectory, "navigation.scm") + "\0" + DiagnosticErrorQuery));
			return $"{definition.Library}:TreeSitter.DotNet-1.3.0:{queryHash}";
		});

	public FileFacts Extract(PreparedDependencySource source, DependencyFactsLimits limits) =>
		Extract(source, limits, CancellationToken.None);

	public IReadOnlyList<NavigationDeclaration> ExtractNavigation(
		string relativePath,
		string source,
		string contentFingerprint,
		CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
		ArgumentNullException.ThrowIfNull(source);
		ArgumentException.ThrowIfNullOrWhiteSpace(contentFingerprint);
		var language = ForPath(relativePath);
		if (language == LanguageId.Unsupported)
			return [];

		cancellationToken.ThrowIfCancellationRequested();
		var runtime = GetRuntime(language);
		using var lease = runtime.Rent(_workerBudget);
		using var tree = lease.Parser.Parse(source) ??
			throw new InvalidOperationException("Tree-sitter returned no syntax tree.");
		Interlocked.Increment(ref _parseCount);
		return CaptureNavigation(
			runtime.Navigation,
			tree.RootNode,
			language,
			relativePath,
			contentFingerprint,
			cancellationToken);
	}

	public FileFacts Extract(
		PreparedDependencySource source,
		DependencyFactsLimits limits,
		CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		if (source.PreparedStatus != DependencyFileStatus.Supported)
			return StatusOnly(source, source.PreparedStatus, source.PreparedStatusReason);
		if (source.Source.Length > limits.MaximumCharactersPerFile)
			return StatusOnly(source, DependencyFileStatus.ExtractionFailed,
				$"file exceeds the {limits.MaximumCharactersPerFile} character parse limit");

		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			var runtime = GetRuntime(source.LanguageId);
			using var lease = runtime.Rent(_workerBudget);
			using var tree = lease.Parser.Parse(source.Source) ??
				throw new InvalidOperationException("Tree-sitter returned no syntax tree.");
			Interlocked.Increment(ref _parseCount);
			var (declarations, references, errorKinds, rawCaptureLimitExceeded) = CaptureFacts(
				runtime.Facts,
				tree.RootNode,
				limits.MaximumRawCapturesPerFile,
				cancellationToken);
			if (rawCaptureLimitExceeded)
				return StatusOnly(source, DependencyFileStatus.ExtractionFailed, "fact limit exceeded");
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
			var result = runtime.Adapter.Extract(context, limits);
			var navigation = CaptureNavigation(
				runtime.Navigation,
				tree.RootNode,
				source.LanguageId,
				source.RelativePath,
				source.ContentFingerprint,
				cancellationToken);
			Interlocked.Add(ref _adapterVisitedRanges, context.Work.VisitedRanges);
			Interlocked.Add(ref _adapterComparisons, context.Work.Comparisons);
			Interlocked.Add(ref _createdFacts,
				result.Declarations.Count + result.Imports.Count + result.References.Count);
			return result with { NavigationDeclarations = navigation };
		}
		catch (Exception exception) when (exception is
		       IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			return StatusOnly(source, DependencyFileStatus.ExtractionFailed,
				"dependency grammar could not be loaded") with { CanCache = false };
		}
		catch (Exception exception) when (exception is
		       DllNotFoundException or BadImageFormatException or
		       EntryPointNotFoundException or InvalidOperationException)
		{
			return StatusOnly(source, DependencyFileStatus.ExtractionFailed,
				"dependency grammar could not be loaded");
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
					var navigation = new Query(language, ReadQuery(definition.QueryDirectory, "navigation.scm"));
					Interlocked.Increment(ref _compiledQuerySetCount);
					return new LanguageRuntime(language, facts, navigation, definition.Adapter);
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

	private (
		IReadOnlyList<DependencySyntaxCapture> Declarations,
		IReadOnlyList<DependencySyntaxCapture> References,
		IReadOnlyDictionary<string, int> ErrorKinds,
		bool RawCaptureLimitExceeded) CaptureFacts(
		Query query,
		Node root,
		int rawCaptureLimit,
		CancellationToken cancellationToken)
	{
		using var cursor = query.Execute(root);
		var declarations = new List<DependencySyntaxCapture>();
		var references = new List<DependencySyntaxCapture>();
		var errorKinds = new Dictionary<string, int>(StringComparer.Ordinal);
		var rawCapturesVisited = 0L;
		var createdCaptures = 0L;
		var materialization = new NodeTextMaterializationCounter();
		var rawCaptureLimitExceeded = false;
		try
		{
			foreach (var capture in cursor.Captures)
			{
				if ((rawCapturesVisited & 255) == 0)
					cancellationToken.ThrowIfCancellationRequested();
				rawCapturesVisited++;
				if (rawCapturesVisited > rawCaptureLimit)
				{
					rawCaptureLimitExceeded = true;
					break;
				}
				if (capture.Name == "diagnostic.error")
				{
					var kinds = capture.Node.Children.Where(static child => child.IsNamed)
						.Select(static child => child.Type).Distinct().ToArray();
					if (kinds.Length == 0) kinds = ["<token>"];
					foreach (var kind in kinds)
						errorKinds[kind] = errorKinds.GetValueOrDefault(kind) + 1;
					continue;
				}
				var created = TryCreateCapture(capture.Name, capture.Node, materialization);
				if (created is null)
					continue;
				var target = IsDeclarationCapture(capture.Name) ? declarations : references;
				target.Add(created);
				createdCaptures++;
			}
		}
		finally
		{
			Interlocked.Add(ref _rawCapturesVisited, rawCapturesVisited);
			Interlocked.Add(ref _createdCaptures, createdCaptures);
			Interlocked.Add(ref _materializedCharacters, materialization.Characters);
		}
		return (
			declarations.OrderBy(static capture => capture.StartIndex)
				.ThenBy(static capture => capture.Name, StringComparer.Ordinal).ToArray(),
			references.OrderBy(static capture => capture.StartIndex)
				.ThenBy(static capture => capture.Name, StringComparer.Ordinal).ToArray(),
			errorKinds,
			rawCaptureLimitExceeded);
	}

	private static IReadOnlyList<NavigationDeclaration> CaptureNavigation(
		Query query,
		Node root,
		LanguageId language,
		string relativePath,
		string contentFingerprint,
		CancellationToken cancellationToken)
	{
		using var cursor = query.Execute(root);
		var declarations = new List<NavigationDeclaration>();
		var seen = new HashSet<(int Start, int End, NavigationSymbolKind Kind, string Name)>();
		var javaNames = language == LanguageId.Java
			? new Dictionary<string, int>(StringComparer.Ordinal)
			: null;
		var visited = 0;
		string? fileScopedNamespace = language == LanguageId.Rust ? RustModulePath(relativePath) : null;
		foreach (var capture in cursor.Captures)
		{
			if ((visited++ & 255) == 0)
				cancellationToken.ThrowIfCancellationRequested();
			var name = ReadNavigationName(capture.Node, language);
			if (string.IsNullOrWhiteSpace(name))
				continue;
			if (language == LanguageId.CSharp && capture.Node.Type == "file_scoped_namespace_declaration")
				fileScopedNamespace = name;
			else if (language == LanguageId.Java && capture.Node.Type == "package_declaration")
				fileScopedNamespace = name;
			var owners = ReadNavigationOwners(capture.Node, language).ToList();
			if (fileScopedNamespace is not null &&
			    capture.Node.Type is not ("file_scoped_namespace_declaration" or "package_declaration"))
			{
				owners.Insert(0, fileScopedNamespace);
			}
			var separator = language == LanguageId.Rust ? "::" : ".";
			var owner = owners.Count == 0 ? null : string.Join(separator, owners);
			var qualifiedName = owner is null ? name : $"{owner}{separator}{name}";
			if (javaNames is not null && capture.Node.Type != "package_declaration")
			{
				var ordinal = javaNames.GetValueOrDefault(qualifiedName) + 1;
				javaNames[qualifiedName] = ordinal;
				if (ordinal > 1) qualifiedName += $"#{ordinal}";
			}
			var kind = capture.Name switch
			{
				"navigation.method" => NavigationSymbolKind.Method,
				"navigation.property" => NavigationSymbolKind.Property,
				"navigation.field" => NavigationSymbolKind.Field,
				"navigation.function" => NavigationSymbolKind.Function,
				"navigation.module" => NavigationSymbolKind.Module,
				_ => NavigationSymbolKind.Type
			};
			var start = checked((int)capture.Node.StartPosition.Row + 1);
			var end = checked((int)capture.Node.EndPosition.Row + 1);
			if (seen.Add((checked((int)capture.Node.StartIndex), checked((int)capture.Node.EndIndex), kind, qualifiedName)))
			{
				declarations.Add(new NavigationDeclaration(
					qualifiedName,
					kind,
					owner,
					start,
					end,
					contentFingerprint)
				{
					StartIndex = checked((int)capture.Node.StartIndex),
					EndIndex = checked((int)capture.Node.EndIndex)
				});
			}
		}
		return declarations
			.OrderBy(static declaration => declaration.StartLine)
			.ThenBy(static declaration => declaration.EndLine)
			.ThenBy(static declaration => declaration.Name, StringComparer.Ordinal)
			.ToArray();
	}

	private static string? ReadNavigationName(Node node, LanguageId language)
	{
		var named = node.GetChildForField("name") ?? node.GetChildForField("key");
		if (named is not null)
			return NormalizeNavigationName(named.Text);
		if (language == LanguageId.Java && node.Type == "package_declaration")
			return NormalizeNavigationName(node.NamedChildren.LastOrDefault()?.Text ?? string.Empty);
		if (language == LanguageId.Rust && node.Type == "impl_item")
		{
			var implementedType = node.GetChildForField("type")?.Text;
			var implementedTrait = node.GetChildForField("trait")?.Text;
			if (!string.IsNullOrWhiteSpace(implementedType))
				return string.IsNullOrWhiteSpace(implementedTrait)
					? $"impl<{implementedType}>"
					: $"impl<{implementedTrait} for {implementedType}>";
		}
		if (language == LanguageId.Rust && node.Type == "let_declaration")
			return NormalizeNavigationName(node.GetChildForField("pattern")?.Text ?? string.Empty);

		if (language == LanguageId.Go)
		{
			if (node.Type == "short_var_declaration")
				return FirstIdentifier(node.GetChildForField("left")?.Text);
			if (node.Type is "field_declaration" or "var_spec")
				return FirstIdentifier(node.Text);
		}
		return null;
	}

	private static IReadOnlyList<string> ReadNavigationOwners(Node node, LanguageId language)
	{
		var owners = new List<string>();
		for (var parent = node.Parent; parent is not null; parent = parent.Parent)
		{
			if (!IsNavigationOwner(parent.Type, language))
				continue;
			var name = ReadNavigationName(parent, language);
			if (!string.IsNullOrWhiteSpace(name))
				owners.Add(name);
		}
		owners.Reverse();
		if (language == LanguageId.Go && node.Type == "method_declaration")
		{
			var receiver = GoReceiverType(node.GetChildForField("receiver")?.Text);
			if (!string.IsNullOrWhiteSpace(receiver))
				owners.Add(receiver);
		}
		return owners;
	}

	private static bool IsNavigationOwner(string nodeType, LanguageId language) => language switch
	{
		LanguageId.CSharp => nodeType is "namespace_declaration" or "file_scoped_namespace_declaration" or
			"class_declaration" or "struct_declaration" or "interface_declaration" or "record_declaration" or
			"enum_declaration" or "method_declaration" or "local_function_statement" or "property_declaration",
		LanguageId.Python => nodeType is "class_definition" or "function_definition",
		LanguageId.TypeScript or LanguageId.Tsx or LanguageId.JavaScript => nodeType is
			"class_declaration" or "abstract_class_declaration" or "interface_declaration" or
			"internal_module" or "function_declaration" or "generator_function_declaration" or
			"method_definition" or "variable_declarator" or "pair",
		LanguageId.Go => nodeType is "type_spec" or "function_declaration" or "method_declaration",
		LanguageId.Java => nodeType is "class_declaration" or "interface_declaration" or
			"enum_declaration" or "record_declaration" or "annotation_type_declaration" or
			"method_declaration" or "constructor_declaration" or "compact_constructor_declaration",
		LanguageId.Rust => nodeType is "mod_item" or "struct_item" or "enum_item" or "trait_item" or
			"union_item" or "impl_item" or "function_item",
		_ => false
	};

	private static string? NormalizeNavigationName(string value)
	{
		var name = value.Trim();
		if (name.Length >= 2 && name[0] is '\'' or '"' && name[^1] == name[0])
			name = name[1..^1];
		return name.Length == 0 ? null : name;
	}

	private static string? FirstIdentifier(string? value)
	{
		if (string.IsNullOrEmpty(value))
			return null;
		for (var start = 0; start < value.Length; start++)
		{
			if (!(value[start] == '_' || char.IsLetter(value[start])))
				continue;
			var end = start + 1;
			while (end < value.Length && (value[end] == '_' || char.IsLetterOrDigit(value[end])))
				end++;
			return value[start..end];
		}
		return null;
	}

	private static string? GoReceiverType(string? receiver)
	{
		if (string.IsNullOrWhiteSpace(receiver))
			return null;
		string? result = null;
		for (var index = 0; index < receiver.Length;)
		{
			if (!(receiver[index] == '_' || char.IsLetter(receiver[index])))
			{
				index++;
				continue;
			}
			var end = index + 1;
			while (end < receiver.Length && (receiver[end] == '_' || char.IsLetterOrDigit(receiver[end])))
				end++;
			result = receiver[index..end];
			index = end;
		}
		return result;
	}

	private static string RustModulePath(string relativePath)
	{
		var portable = Normalize(relativePath);
		var sourceMarker = portable.LastIndexOf("/src/", StringComparison.Ordinal);
		if (sourceMarker >= 0) portable = portable[(sourceMarker + "/src/".Length)..];
		else if (portable.StartsWith("src/", StringComparison.Ordinal)) portable = portable["src/".Length..];
		portable = Path.ChangeExtension(portable, null) ?? portable;
		if (portable is "lib" or "main" or "mod") return string.Empty;
		if (portable.EndsWith("/mod", StringComparison.Ordinal)) portable = portable[..^"/mod".Length];
		return string.Join("::", portable.Split('/', StringSplitOptions.RemoveEmptyEntries));
	}

	private static bool IsDeclarationCapture(string captureName) =>
		captureName.StartsWith("declaration.", StringComparison.Ordinal) ||
		captureName is "context.namespace" or "context.using";

	private static DependencySyntaxCapture? TryCreateCapture(
		string captureName,
		Node node,
		NodeTextMaterializationCounter materialization)
	{
		if (captureName == "context.type_parameters")
		{
			var owner = node.Parent;
			while (owner is not null && !IsTypeParameterOwner(owner.Type))
				owner = owner.Parent;
			var text = materialization.Read(node);
			return new DependencySyntaxCapture(
				captureName,
				node.Type,
				text,
				checked((int)node.StartPosition.Row + 1),
				checked((int)(owner?.StartIndex ?? node.StartIndex)),
				checked((int)(owner?.EndIndex ?? node.EndIndex)),
				Evidence: OneLineEvidence(text));
		}
		string? moduleCallName = null;
		if (captureName == "import.call" &&
		    !TryReadSupportedModuleCall(node, materialization, out moduleCallName))
			return null;

		if (captureName.StartsWith("import.", StringComparison.Ordinal))
		{
			var importSyntax = CreateImportSyntax(captureName, node, materialization, moduleCallName);
			var importEvidence = CreateCompactImportEvidence(captureName, importSyntax);
			return CreateCapture(captureName, node, importEvidence, null, 0, false, false,
				FindImportOwner(captureName, node, materialization), importSyntax, evidence: importEvidence);
		}
		if (captureName == "context.namespace" && node.Type == "package_declaration")
		{
			var packageNameNode = node.NamedChildren.LastOrDefault();
			var packageName = packageNameNode is null ? null : materialization.Read(packageNameNode);
			return CreateCapture(
				captureName,
				node,
				packageName ?? string.Empty,
				packageName,
				0,
				false,
				false,
				capturedNameStartIndex: packageNameNode is null ? -1 : checked((int)packageNameNode.StartIndex),
				evidence: packageName ?? string.Empty);
		}

		var isCompact = captureName.StartsWith("declaration.", StringComparison.Ordinal) ||
			captureName is "context.namespace" or "context.type_parameter_owner";
		if (!isCompact)
		{
			var text = materialization.Read(node);
			return CreateCapture(captureName, node, text, null, 0, false, false,
				captureName == "context.module_assignment"
					? FindContainingDeclaration(node, materialization)
					: null,
				evidence: OneLineEvidence(text));
		}

		var nameNode = node.GetChildForField("name");
		var capturedName = nameNode is null ? null : materialization.Read(nameNode);
		var capturedNameStartIndex = nameNode is null ? -1 : checked((int)nameNode.StartIndex);
		var typeParameters = node.Children.FirstOrDefault(static child =>
			child.Type is "type_parameter_list" or "type_parameters");
		var genericArity = typeParameters is null ? 0 : CountGenericArity(materialization.Read(typeParameters));
		var evidence = string.IsNullOrEmpty(capturedName)
			? string.Empty
			: capturedName + (genericArity == 0 ? string.Empty : $"`{genericArity}");
		var isFileLocal = false;
		var isStatic = false;
		foreach (var child in node.Children)
		{
			if (child.Type != "modifier") continue;
			var modifier = materialization.Read(child);
			if (modifier == "file") isFileLocal = true;
			else if (modifier == "static") isStatic = true;
		}

		isFileLocal = isFileLocal && captureName.StartsWith("declaration.", StringComparison.Ordinal);
		return CreateCapture(captureName, node, evidence, capturedName, genericArity, isFileLocal,
			isStatic, FindContainingDeclaration(node, materialization),
			capturedNameStartIndex: capturedNameStartIndex, evidence: evidence);
	}

	private static bool TryReadSupportedModuleCall(
		Node node,
		NodeTextMaterializationCounter materialization,
		out string? functionName)
	{
		var function = node.GetChildForField("function");
		functionName = function is null ? null : materialization.Read(function);
		return (functionName is "require" or "import") &&
		       (functionName != "require" || !HasRequireParameter(node, materialization));
	}

	private static bool HasRequireParameter(
		Node call,
		NodeTextMaterializationCounter materialization)
	{
		for (var ancestor = call.Parent; ancestor is not null; ancestor = ancestor.Parent)
		{
			var parameters = ancestor.GetChildForField("parameters");
			if (parameters is not null && ContainsIdentifier(parameters, "require", materialization))
				return true;
			var parameter = ancestor.GetChildForField("parameter");
			if (parameter is not null && ContainsIdentifier(parameter, "require", materialization))
				return true;
		}
		return false;
	}

	private static bool ContainsIdentifier(
		Node node,
		string identifier,
		NodeTextMaterializationCounter materialization)
	{
		if (node.Type == "identifier" && materialization.Read(node) == identifier)
			return true;
		return node.NamedChildren.Any(child => ContainsIdentifier(child, identifier, materialization));
	}

	private static string CreateCompactImportEvidence(
		string captureName,
		DependencyImportSyntax? syntax)
	{
		if (captureName is "import.esm" or "import.export")
			return OneLineEvidence($"{(captureName == "import.export" ? "export" : "import")} {syntax?.Specifier ?? string.Empty}");
		if (captureName == "import.call")
		{
			var function = syntax?.ImportKind == ModuleImportKind.Require ? "require" : "import";
			var argument = syntax?.HasLiteralSpecifier == true ? syntax.Specifier : "<non-literal>";
			return OneLineEvidence($"{function}({argument})");
		}
		if (captureName == "import.direct")
			return "import " + OneLineEvidence(string.Join(", ", syntax?.Bindings.Select(static binding => binding.Name) ?? []));
		return "from " + OneLineEvidence(syntax?.Specifier ?? string.Empty) + " import";
	}

	private static string OneLineEvidence(string value)
	{
		var prefixLength = Math.Min(value.Length, 240);
		var line = value.AsSpan(0, prefixLength).ToString().Replace('\r', ' ').Replace('\n', ' ').Trim();
		if (value.Length <= prefixLength) return line;
		return line.Length <= 237 ? line + "..." : line[..237] + "...";
	}

	private static string? FindImportOwner(
		string captureName,
		Node node,
		NodeTextMaterializationCounter materialization)
	{
		if (captureName is not ("import.direct" or "import.from")) return null;
		var declaration = FindContainingDeclaration(node, materialization);
		if (declaration is not null) return declaration;
		for (var parent = node.Parent; parent is not null; parent = parent.Parent)
			if (parent.Type is "if_statement" or "for_statement" or "while_statement" or
			    "try_statement" or "with_statement" or "match_statement")
				return "$conditional-import";
		return null;
	}

	private static DependencySyntaxCapture CreateCapture(
		string captureName,
		Node node,
		string text,
		string? capturedName,
		int genericArity,
		bool isFileLocal,
		bool isStatic,
		string? containingDeclaration = null,
		DependencyImportSyntax? importSyntax = null,
		int capturedNameStartIndex = -1,
		string? evidence = null) =>
		new(
			captureName,
			node.Type,
			text,
			checked((int)node.StartPosition.Row + 1),
			checked((int)node.StartIndex),
			checked((int)node.EndIndex),
			capturedName,
			genericArity,
			isFileLocal,
			isStatic,
			containingDeclaration,
			importSyntax,
			capturedNameStartIndex,
			evidence,
			checked((int)node.EndPosition.Row + 1));

	private static string? FindContainingDeclaration(
		Node node,
		NodeTextMaterializationCounter materialization)
	{
		for (var parent = node.Parent; parent is not null; parent = parent.Parent)
		{
			if (parent.Type is not ("class_definition" or "function_definition")) continue;
			var nameNode = parent.GetChildForField("name");
			var name = nameNode is null ? null : materialization.Read(nameNode);
			if (!string.IsNullOrWhiteSpace(name)) return name;
		}
		return null;
	}

	private static DependencyImportSyntax? CreateImportSyntax(
		string captureName,
		Node node,
		NodeTextMaterializationCounter materialization,
		string? moduleCallName)
	{
		if (captureName is "import.esm" or "import.export")
		{
			var source = node.GetChildForField("source");
			var specifier = source is null ? null : ReadJavaScriptStringLiteral(source, materialization);
			return source is null
				? null
				: new DependencyImportSyntax(specifier ?? string.Empty, 0, [], specifier is not null);
		}
		if (captureName == "import.call")
		{
			var argument = node.GetChildForField("arguments")?.NamedChildren
				.Where(static child => child.Type != "comment")
				.FirstOrDefault();
			var specifier = argument is null ? null : ReadJavaScriptStringLiteral(argument, materialization);
			return new DependencyImportSyntax(
				specifier ?? string.Empty,
				0,
				[],
				specifier is not null,
				moduleCallName == "require" ? ModuleImportKind.Require : ModuleImportKind.DynamicImport);
		}
		if (captureName == "import.direct")
		{
			var bindings = ReadPythonBindings(node.GetChildrenForField("name"), materialization);
			return new DependencyImportSyntax(string.Empty, 0, bindings);
		}
		if (captureName == "import.from")
		{
			var moduleNode = node.GetChildForField("module_name");
			var moduleText = moduleNode is null ? string.Empty : materialization.Read(moduleNode);
			var relativeLevel = moduleText.TakeWhile(static character => character == '.').Count();
			var bindings = ReadPythonBindings(node.GetChildrenForField("name"), materialization);
			if (node.NamedChildren.Any(static child => child.Type == "wildcard_import"))
				bindings = [new DependencyImportBinding("*", null, true)];
			return new DependencyImportSyntax(moduleText[relativeLevel..], relativeLevel, bindings);
		}
		if (captureName == "import.java")
		{
			var text = materialization.Read(node).Trim();
			if (!text.StartsWith("import ", StringComparison.Ordinal) || !text.EndsWith(';'))
				return null;
			var specifier = text["import ".Length..^1].Trim();
			if (specifier.StartsWith("static ", StringComparison.Ordinal))
				specifier = specifier["static ".Length..].Trim();
			var wildcard = specifier.EndsWith(".*", StringComparison.Ordinal);
			if (wildcard)
				specifier = specifier[..^2];
			return specifier.Length == 0
				? null
				: new DependencyImportSyntax(
					specifier,
					0,
					[new DependencyImportBinding(specifier.Split('.').Last(), null, wildcard)]);
		}
		if (captureName == "import.rust")
		{
			var text = materialization.Read(node).Trim();
			if (!text.StartsWith("use ", StringComparison.Ordinal) || !text.EndsWith(';')) return null;
			return new DependencyImportSyntax(text["use ".Length..^1].Trim(), 0, []);
		}
		if (captureName == "import.rust_module")
		{
			if (node.GetChildForField("body") is not null) return null;
			var name = node.GetChildForField("name");
			return name is null ? null : new DependencyImportSyntax(materialization.Read(name), 0, []);
		}
		return null;
	}

	private static IReadOnlyList<DependencyImportBinding> ReadPythonBindings(
		IEnumerable<Node> nodes,
		NodeTextMaterializationCounter materialization) =>
		nodes.Select(node =>
		{
			if (node.Type != "aliased_import")
				return new DependencyImportBinding(materialization.Read(node), null, node.Type == "wildcard_import");
			var nameNode = node.GetChildForField("name");
			var aliasNode = node.GetChildForField("alias");
			return new DependencyImportBinding(
				nameNode is null ? string.Empty : materialization.Read(nameNode),
				aliasNode is null ? null : materialization.Read(aliasNode));
		}).Where(static binding => binding.Name.Length > 0).ToArray();

	private static string? ReadJavaScriptStringLiteral(
		Node node,
		NodeTextMaterializationCounter materialization)
	{
		if (node.Type != "string") return null;
		var text = materialization.Read(node);
		if (text.Length < 2 || text[0] is not ('\'' or '"') || text[^1] != text[0]) return null;
		var result = new StringBuilder(text.Length - 2);
		for (var index = 1; index < text.Length - 1; index++)
		{
			var character = text[index];
			if (character != '\\')
			{
				result.Append(character);
				continue;
			}
			if (++index >= text.Length - 1) return null;
			var escaped = text[index];
			switch (escaped)
			{
				case '\\': result.Append('\\'); break;
				case '\'': result.Append('\''); break;
				case '"': result.Append('"'); break;
				case 'n': result.Append('\n'); break;
				case 'r': result.Append('\r'); break;
				case 't': result.Append('\t'); break;
				case 'b': result.Append('\b'); break;
				case 'f': result.Append('\f'); break;
				case 'v': result.Append('\v'); break;
				case '0': result.Append('\0'); break;
				case 'x':
					if (!TryReadHexEscape(text, ref index, 2, out var hex)) return null;
					result.Append((char)hex);
					break;
				case 'u':
					if (!TryReadHexEscape(text, ref index, 4, out var unicode)) return null;
					result.Append((char)unicode);
					break;
				case '\r':
					if (index + 1 < text.Length - 1 && text[index + 1] == '\n') index++;
					break;
				case '\n':
					break;
				default:
					result.Append(escaped);
					break;
			}
		}
		return result.ToString();
	}

	private static bool TryReadHexEscape(string text, ref int index, int digits, out int value)
	{
		value = 0;
		if (index + digits >= text.Length) return false;
		for (var offset = 1; offset <= digits; offset++)
		{
			var digit = text[index + offset];
			var numeric = digit switch
			{
				>= '0' and <= '9' => digit - '0',
				>= 'a' and <= 'f' => digit - 'a' + 10,
				>= 'A' and <= 'F' => digit - 'A' + 10,
				_ => -1
			};
			if (numeric < 0) return false;
			value = (value << 4) | numeric;
		}
		index += digits;
		return true;
	}

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
		status, reason, false, new Dictionary<string, int>(), [], [], [], [], new Dictionary<string, string>(), [], new Dictionary<string, string>(), [])
	{
		CanCache = source.CanCache
	};

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
		".go" => LanguageId.Go,
		".java" => LanguageId.Java,
		".rs" => LanguageId.Rust,
		_ => LanguageId.Unsupported
	};

	private static LanguageId LanguageFamily(LanguageId language) => language is LanguageId.JavaScript or LanguageId.Tsx
		? LanguageId.TypeScript
		: language;
	private static string Normalize(string path) => path.Replace('\\', '/');
	private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
	private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
	private static bool IsWithin(string root, string path)
	{
		var relative = Path.GetRelativePath(root, path);
		return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		lock (_preparedSourceTrimSync)
		{
			_preparedSources.Clear();
			_preparedSourceOrder.Clear();
			_preparedSourceBytes = 0;
		}
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
		LanguageId LanguageId,
		int MaximumCharacters);

	private sealed class PreparedSourceCacheEntry(
		PreparedSourceCacheKey key,
		long generation,
		string? contentIdentity,
		Lazy<Task<PreparedSourceContent>> value)
	{
		public PreparedSourceCacheKey Key { get; } = key;
		public long Generation { get; } = generation;
		public string? ContentIdentity { get; } = contentIdentity;
		public Lazy<Task<PreparedSourceContent>> Value { get; } = value;
		public long RegisteredWeight { get; set; }
		public LinkedListNode<PreparedSourceCacheEntry>? OrderNode { get; set; }
	}

	private static bool IsTypeParameterOwner(string nodeType) => nodeType is
		"class_declaration" or "struct_declaration" or "interface_declaration" or
		"record_declaration" or "delegate_declaration" or "method_declaration" or
		"local_function_statement";

	internal readonly record struct PreparedSourceCacheState(
		int Entries,
		int EvictionEntries,
		long RetainedBytes);

	private sealed class ScopeOwnerIndex
	{
		private readonly IReadOnlyDictionary<LanguageId, DependencyScopeDescriptor[]> _byLanguage;

		public ScopeOwnerIndex(IEnumerable<DependencyScopeDescriptor> scopes) =>
			_byLanguage = scopes
				.GroupBy(static scope => LanguageFamily(scope.LanguageId))
				.ToDictionary(
					static group => group.Key,
					static group => group.OrderByDescending(static scope => scope.Root.Length).ToArray());

		public string Resolve(LanguageId language, string fullPath)
		{
			var family = LanguageFamily(language);
			if (_byLanguage.TryGetValue(family, out var candidates))
				foreach (var candidate in candidates)
					if (IsWithin(candidate.Root, fullPath))
						return candidate.ScopeId;
			return $"root:{family.ToString().ToLowerInvariant()}";
		}
	}

	internal sealed class BoundedDependencySourceReader(FileContentReadStreamOpener? sourceOpener = null)
	{
		private const int BufferSize = 64 * 1024;
		private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
		private static readonly Encoding StrictUtf16Le = new UnicodeEncoding(false, true, true);
		private static readonly Encoding StrictUtf16Be = new UnicodeEncoding(true, true, true);
		private static readonly Encoding StrictUtf32Le = new UTF32Encoding(false, true, true);
		private static readonly Encoding StrictUtf32Be = new UTF32Encoding(true, true, true);
		private long _lastBytesRead;
		private int _lastByteBufferCapacity;
		private int _lastCharacterBufferCapacity;

		internal long LastBytesRead => Interlocked.Read(ref _lastBytesRead);
		internal int LastByteBufferCapacity => Volatile.Read(ref _lastByteBufferCapacity);
		internal int LastCharacterBufferCapacity => Volatile.Read(ref _lastCharacterBufferCapacity);

		internal async Task<BoundedDependencySourceRead> ReadAsync(
			string path,
			int maximumCharacters,
			CancellationToken cancellationToken)
		{
			byte[]? byteBuffer = null;
			char[]? characterBuffer = null;
			var bytesReadTotal = 0L;
			var charactersWritten = 0;
			try
			{
				await using var stream = sourceOpener is null
					? new FileStream(
						path,
						FileMode.Open,
						FileAccess.Read,
						FileShare.Read | FileShare.Delete,
						BufferSize,
						FileOptions.Asynchronous | FileOptions.SequentialScan)
					: sourceOpener(path, BufferSize, FileShare.Read | FileShare.Delete, asynchronous: true);
				var length = stream.Length;
				var lastWrite = File.GetLastWriteTimeUtc(stream.SafeFileHandle).Ticks;
				var byteBufferSize = checked((int)Math.Clamp(length, 1, BufferSize));
				byteBuffer = ArrayPool<byte>.Shared.Rent(byteBufferSize);
				// Digest the bytes in the pass that decodes them, so a caller that hashed the same
				// file earlier can compare without opening it again. It covers the whole stream,
				// preamble included, and is only reported when the stream was read to its end.
				using var observed = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
				Decoder? decoder = null;
				StringBuilder? source = null;
				while (true)
				{
					cancellationToken.ThrowIfCancellationRequested();
					var bytesRead = await stream.ReadAsync(
						byteBuffer.AsMemory(0, byteBufferSize),
						cancellationToken).ConfigureAwait(false);
					if (bytesRead == 0)
						break;
					bytesReadTotal += bytesRead;
					observed.AppendData(byteBuffer.AsSpan(0, bytesRead));
					var input = byteBuffer.AsSpan(0, bytesRead);
					if (decoder is null)
					{
						var (encoding, preambleLength) = DetectEncoding(input);
						decoder = encoding.GetDecoder();
						var characterBufferSize = CharacterBufferSize(length - preambleLength, encoding, maximumCharacters);
						characterBuffer = ArrayPool<char>.Shared.Rent(characterBufferSize);
						source = new StringBuilder(characterBufferSize);
						input = input[preambleLength..];
					}
					while (!input.IsEmpty)
					{
						var decodeBuffer = characterBuffer ??
							throw new InvalidOperationException("The dependency source decoder has no character buffer.");
						var remainingCharacters = (long)maximumCharacters + 1 - charactersWritten;
						if (remainingCharacters <= 0)
							return TooLarge(length, lastWrite, maximumCharacters);
						decoder.Convert(
							input,
							decodeBuffer.AsSpan(0, checked((int)Math.Min(decodeBuffer.Length, remainingCharacters))),
							flush: false,
							out var bytesUsed,
							out var charactersUsed,
							out _);
						input = input[bytesUsed..];
						charactersWritten += charactersUsed;
						if (charactersUsed > 0)
							source!.Append(decodeBuffer, 0, charactersUsed);
						if (charactersWritten > maximumCharacters)
							return TooLarge(length, lastWrite, maximumCharacters);
						if (bytesUsed == 0 && charactersUsed == 0)
							throw new IOException("The bounded dependency source decoder made no progress.");
					}
				}

				if (decoder is null)
				{
					decoder = StrictUtf8.GetDecoder();
					characterBuffer = ArrayPool<char>.Shared.Rent(1);
					source = new StringBuilder(1);
				}
				var finalBuffer = characterBuffer ??
					throw new InvalidOperationException("The dependency source decoder has no character buffer.");
				var finalCapacity = checked((int)Math.Min(finalBuffer.Length, (long)maximumCharacters + 1 - charactersWritten));
				decoder.Convert(
					ReadOnlySpan<byte>.Empty,
					finalBuffer.AsSpan(0, finalCapacity),
					flush: true,
					out _,
					out var finalCharacters,
					out var completed);
				charactersWritten += finalCharacters;
				if (finalCharacters > 0)
					source!.Append(finalBuffer, 0, finalCharacters);
				if (charactersWritten > maximumCharacters)
					return TooLarge(length, lastWrite, maximumCharacters);
				if (!completed)
					throw new IOException("The bounded dependency source decoder did not complete.");
				if (stream.Length != length || File.GetLastWriteTimeUtc(stream.SafeFileHandle).Ticks != lastWrite)
					throw new IOException("The dependency source changed while it was being read.");
				var content = source!.ToString();
				if (content.AsSpan().Contains('\0'))
				{
					return new BoundedDependencySourceRead(
						MetadataFingerprint("binary", length, lastWrite),
						string.Empty,
						DependencyFileStatus.ExtractionFailed,
						"source is binary",
						ObservedContentDigest: Convert.ToHexString(observed.GetCurrentHash()));
				}
				return new BoundedDependencySourceRead(
					ContentFingerprint.Compute(content.AsSpan()).ToHexString().ToLowerInvariant(),
					content,
					DependencyFileStatus.Supported,
					null,
					ObservedContentDigest: Convert.ToHexString(observed.GetCurrentHash()));
			}
			catch (DecoderFallbackException)
			{
				return new BoundedDependencySourceRead(
					MetadataFingerprint("unsupported-encoding", 0, 0),
					string.Empty,
					DependencyFileStatus.ExtractionFailed,
					"source uses an unsupported encoding");
			}
			finally
			{
				Interlocked.Exchange(ref _lastBytesRead, bytesReadTotal);
				Volatile.Write(ref _lastByteBufferCapacity, byteBuffer?.Length ?? 0);
				Volatile.Write(ref _lastCharacterBufferCapacity, characterBuffer?.Length ?? 0);
				if (byteBuffer is not null)
				{
					CryptographicOperations.ZeroMemory(byteBuffer);
					ArrayPool<byte>.Shared.Return(byteBuffer);
				}
				if (characterBuffer is not null)
				{
					CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characterBuffer.AsSpan()));
					ArrayPool<char>.Shared.Return(characterBuffer);
				}
			}
		}

		private static int CharacterBufferSize(long contentBytes, Encoding encoding, int maximumCharacters)
		{
			var divisor = encoding.CodePage switch
			{
				1200 or 1201 => 2,
				12000 or 12001 => 4,
				_ => 1
			};
			var estimatedCharacters = Math.Max(1L, (contentBytes + divisor - 1) / divisor);
			return checked((int)Math.Min(Math.Min(estimatedCharacters, maximumCharacters + 1L), BufferSize));
		}

		private static BoundedDependencySourceRead TooLarge(
			long length,
			long lastWrite,
			int maximumCharacters) => new(
			MetadataFingerprint("character-limit", length, lastWrite),
			string.Empty,
			DependencyFileStatus.ExtractionFailed,
			$"file exceeds the {maximumCharacters} character parse limit");

		private static (Encoding Encoding, int PreambleLength) DetectEncoding(ReadOnlySpan<byte> bytes)
		{
			if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
				return (StrictUtf32Be, 4);
			if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
				return (StrictUtf32Le, 4);
			if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
				return (StrictUtf8, 3);
			if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
				return (StrictUtf16Be, 2);
			if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
				return (StrictUtf16Le, 2);
			return (StrictUtf8, 0);
		}

		private static string MetadataFingerprint(string state, long length, long lastWrite) =>
			Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{state}:{length}:{lastWrite}")));
	}

	internal sealed record BoundedDependencySourceRead(
		string Fingerprint,
		string Source,
		DependencyFileStatus Status,
		string? StatusReason,
		bool CanCache = true,
		string? ObservedContentDigest = null);

	internal readonly record struct DependencyExtractionWorkState(
		long RawCapturesVisited,
		long CreatedCaptures,
		long MaterializedCharacters,
		long AdapterVisitedRanges,
		long AdapterComparisons,
		long CreatedFacts,
		int ParsedFiles);

	private sealed class NodeTextMaterializationCounter
	{
		public long Characters { get; private set; }

		public string Read(Node node)
		{
			var text = node.Text;
			Characters += text.Length;
			return text;
		}
	}

	private sealed record PreparedSourceContent(
		string Fingerprint,
		string ExtractorIdentity,
		string Source,
		DependencyFileStatus Status = DependencyFileStatus.Supported,
		string? StatusReason = null,
		bool CanCache = true,
		string? ObservedContentDigest = null);

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
				[LanguageId.Python] = new("tree-sitter-python", "tree_sitter_python", "python", new PythonDependencyLanguageAdapter()),
				[LanguageId.Go] = new("tree-sitter-go", "tree_sitter_go", "go", new GoDependencyLanguageAdapter()),
				[LanguageId.Java] = new("tree-sitter-java", "tree_sitter_java", "java", new JavaDependencyLanguageAdapter()),
				[LanguageId.Rust] = new("tree-sitter-rust", "tree_sitter_rust", "rust", new RustDependencyLanguageAdapter())
			};
	}

	private sealed class LanguageRuntime(
		Language language,
		Query facts,
		Query navigation,
		IDependencyLanguageAdapter adapter) : IDisposable
	{
		private readonly ConcurrentBag<Parser> _parsers = [];
		private int _retained;
		public Query Facts { get; } = facts;
		public Query Navigation { get; } = navigation;
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
			Navigation.Dispose();
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
