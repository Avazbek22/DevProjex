using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using DevProjex.Application.Dependencies;
using DevProjex.Application.Ranking;
using DevProjex.Infrastructure.Dependencies;

if (args.FirstOrDefault() == "operations")
{
	await OperationRunner.RunAsync(args[1..]);
	return;
}
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

[MemoryDiagnoser]
public class DependencyAlgorithmBenchmarks
{
	private const int AlgorithmSize = 20_000;
	private readonly string[] _hashValues = Enumerable.Range(0, 20_000)
		.Select(index => $"src/feature-{index:D5}/Type{index:D5}.cs")
		.ToArray();
	private readonly IReadOnlyDictionary<string, double?> _rankValuesWithTies = Enumerable.Range(0, 20_000)
		.ToDictionary(index => $"src/{index:D5}.cs", index => (double?)(index % 97), StringComparer.Ordinal);
	private readonly IReadOnlyDictionary<string, double?> _uniqueRankValues = Enumerable.Range(0, 20_000)
		.ToDictionary(index => $"src/{index:D5}.cs", index => (double?)index, StringComparer.Ordinal);
	private readonly System.Reflection.MethodInfo _hash = typeof(DependencyFactsEngine)
		.GetMethod("Hash", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
	private readonly System.Reflection.MethodInfo _normalize = typeof(ImportanceRankingService)
		.GetMethod("RankNormalize", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
	private readonly System.Reflection.MethodInfo _pageRank = typeof(ImportanceRankingService)
		.GetMethod("CalculatePageRank", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
			null, [typeof(IReadOnlyList<(string FullPath, string RelativePath)>), typeof(DependencyIndexSnapshot),
				typeof(CancellationToken), typeof(Action<int>)], null)!;
	private readonly System.Reflection.MethodInfo _aggregate = typeof(DependencyFactsEngine)
		.GetNestedType("DependencyResolver", System.Reflection.BindingFlags.NonPublic)!
		.GetMethod("Aggregate", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
	private readonly object _resolverContext;
	private readonly System.Reflection.MethodInfo _aliasLookup;
	private readonly FileFacts _aliasSource;
	private readonly ReferenceFact _aliasReference;
	private readonly (string FullPath, string RelativePath)[] _pageRankCandidates;
	private readonly DependencyIndexSnapshot _pageRankSnapshot;
	private readonly DependencyEdge[] _edges;

	public DependencyAlgorithmBenchmarks()
	{
		_pageRankCandidates = Enumerable.Range(0, 4_000)
			.Select(index => ($"C:/benchmark/{index:D5}.cs", $"{index:D5}.cs"))
			.ToArray();
		var facts = _pageRankCandidates.Select(candidate => EmptyFacts(candidate.RelativePath)).ToArray();
		var edges = Enumerable.Range(1, facts.Length - 1)
			.Select(index => Edge(facts[index].Path, facts[index - 1].Path, $"Type{index - 1:D5}"))
			.ToArray();
		_pageRankSnapshot = new DependencyIndexSnapshot("C:/benchmark", "manifest", "declarations", facts, [], edges,
			new Dictionary<string, IReadOnlyList<DependencyEdge>>(),
			new Dictionary<string, IReadOnlyList<DependencyEdge>>(),
			new DependencyFactsCoverage(facts.Length, facts.Length, 0, 0,
				new Dictionary<string, int>(), new Dictionary<string, int>()),
			new DependencyIndexMetrics(facts.Length, 0, facts.Length, 0, false))
		{
			FileByPath = facts.ToDictionary(static fact => fact.Path, StringComparer.Ordinal)
		};
		_edges = Enumerable.Range(0, AlgorithmSize)
			.Select(index => Edge($"source-{index % 1000:D4}.cs", $"target-{index % 127:D3}.cs", $"Type{index % 127:D3}"))
			.ToArray();

		_aliasSource = EmptyFacts("Source.cs") with
		{
			CSharpUsingDirectives = [new CSharpUsingDirective("Company.Models", "Alias", 0, int.MaxValue)]
		};
		_aliasReference = new ReferenceFact(EvidenceLayer.TypeReference, "Alias.Type", 0, "type",
			new SourceSite("Source.cs", 1, "Alias.Type")) { SourceStartIndex = 10 };
		var configuration = new DependencyResolverConfiguration("benchmark",
			[new DependencyScopeDescriptor("scope", "C:/benchmark", LanguageId.CSharp, [], null, false,
				new Dictionary<string, IReadOnlyList<string>>(), null, new HashSet<string>(), [], true)],
			new Dictionary<string, PackageMapDescriptor>(), new HashSet<string>(),
			new Dictionary<string, IReadOnlySet<string>>(), new HashSet<string>());
		var resolverType = typeof(DependencyFactsEngine).GetNestedType("ResolverContext",
			System.Reflection.BindingFlags.NonPublic)!;
		_resolverContext = Activator.CreateInstance(resolverType,
			System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
			System.Reflection.BindingFlags.NonPublic, null,
			["C:/benchmark", new[] { _aliasSource }, Array.Empty<DeclarationFact>(), configuration], null)!;
		_aliasLookup = resolverType.GetMethod("TryExpandCSharpAlias",
			System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
	}

	[Benchmark]
	public object HashManifest() => _hash.Invoke(null, [_hashValues])!;

	[Benchmark]
	public object NormalizeRanksWithTies() => _normalize.Invoke(null, [_rankValuesWithTies, CancellationToken.None])!;

	[Benchmark]
	public object NormalizeUniqueRanks() => _normalize.Invoke(null, [_uniqueRankValues, CancellationToken.None])!;

	[Benchmark]
	public object PageRank() => _pageRank.Invoke(null,
		[_pageRankCandidates, _pageRankSnapshot, CancellationToken.None, null])!;

	[Benchmark]
	public object AggregateEdges() => _aggregate.Invoke(null, [_edges])!;

	[Benchmark]
	public object AliasLookup()
	{
		object?[] arguments = [_aliasSource, _aliasReference, null];
		return _aliasLookup.Invoke(_resolverContext, arguments)!;
	}

	private static FileFacts EmptyFacts(string path) => new(path, "scope", LanguageId.CSharp, path, 0,
		DependencyFileStatus.Supported, null, false, new Dictionary<string, int>(), [], [], [], [],
		new Dictionary<string, string>(), [], new Dictionary<string, string>(), []);

	private static DependencyEdge Edge(string source, string target, string reference) => new(source, target,
		EvidenceLayer.TypeReference, ResolutionStatus.Resolved, reference, ["resolved"],
		[new SourceSite(source, 1, reference)], [], false);
}

internal static class OperationRunner
{
	public static async Task RunAsync(string[] args)
	{
		var root = Path.GetFullPath(Required(args, "--root"));
		var output = Path.GetFullPath(Required(args, "--output"));
		var repetitions = int.Parse(Optional(args, "--repetitions") ?? "5");
		var syntheticCount = int.Parse(Optional(args, "--synthetic-count") ?? "0");
		var files = syntheticCount > 0
			? CreateSyntheticFiles(root, syntheticCount)
			: EnumerateCorpus(root);
		var samples = new List<OperationSample>();
		for (var repetition = 0; repetition < repetitions; repetition++)
		{
			using var engine = syntheticCount > 0
				? new DependencyFactsEngine(new SyntheticExtractor(), new SyntheticConfigurationProvider(root))
				: new DependencyFactsEngine(new TreeSitterDependencyFactExtractor(), new FileDependencyConfigurationProvider());
			samples.Add(await MeasureAsync("index-cold", repetition, () => engine.IndexAsync(root, files)));
			samples.Add(await MeasureAsync("index-warm", repetition, () => engine.IndexAsync(root, files)));
			var ranking = new ImportanceRankingService(engine, EmptyHistoryReader.Instance);
			samples.Add(await MeasureAsync("importance", repetition, () => ranking.RankAsync(root, files)));
			var seed = files.First(path => IsSource(path));
			samples.Add(await MeasureAsync("focus", repetition, () => ranking.RankAsync(
				root, files, new FocusRankingRequest([new FocusRankingSeedRequest(Path.GetRelativePath(root, seed), seed)]))));
		}
		var result = new OperationReport(
			root,
			files.Count,
			samples,
			samples.GroupBy(static sample => sample.Operation).Select(group => new OperationSummary(
				group.Key,
				Median(group.Select(static sample => sample.ElapsedMilliseconds)),
				group.Min(static sample => sample.ElapsedMilliseconds),
				group.Max(static sample => sample.ElapsedMilliseconds),
				Median(group.Select(static sample => sample.AllocatedBytes)),
				group.Min(static sample => sample.AllocatedBytes),
				group.Max(static sample => sample.AllocatedBytes))).ToArray());
		Directory.CreateDirectory(Path.GetDirectoryName(output)!);
		await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
	}

	private static async Task<OperationSample> MeasureAsync(string operation, int repetition, Func<Task> action)
	{
		var allocated = GC.GetTotalAllocatedBytes(precise: true);
		var stopwatch = Stopwatch.StartNew();
		await action();
		stopwatch.Stop();
		return new OperationSample(operation, repetition, stopwatch.Elapsed.TotalMilliseconds,
			GC.GetTotalAllocatedBytes(precise: true) - allocated, Process.GetCurrentProcess().PeakWorkingSet64);
	}

	private static List<string> EnumerateCorpus(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
		.Where(path => !path.Split(Path.DirectorySeparatorChar).Any(segment => segment is ".git" or "bin" or "obj" or "node_modules" or "artifacts" or "publish"))
		.Where(path => IsSource(path) || IsControl(path))
		.Order(StringComparer.Ordinal)
		.ToList();

	private static List<string> CreateSyntheticFiles(string root, int count)
	{
		Directory.CreateDirectory(root);
		var files = new List<string>(count);
		for (var index = 0; index < count; index++)
		{
			var path = Path.Combine(root, $"Type{index:D5}.cs");
			if (!File.Exists(path)) File.WriteAllText(path, string.Empty);
			files.Add(path);
		}
		return files;
	}

	private static bool IsSource(string path) => Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".ts" or ".tsx" or ".js" or ".jsx" or ".py";
	private static bool IsControl(string path)
	{
		var name = Path.GetFileName(path);
		return path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
		       name.Equals("package.json", StringComparison.OrdinalIgnoreCase) ||
		       name.StartsWith("tsconfig", StringComparison.OrdinalIgnoreCase) ||
		       name is "pyproject.toml" or "setup.cfg";
	}
	private static string Required(string[] values, string name) => Optional(values, name) ?? throw new ArgumentException($"Missing {name}.");
	private static string? Optional(string[] values, string name)
	{
		var index = Array.IndexOf(values, name);
		return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
	}
	private static double Median(IEnumerable<double> values)
	{
		var ordered = values.Order().ToArray();
		return ordered.Length % 2 == 0 ? (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2 : ordered[ordered.Length / 2];
	}
	private static long Median(IEnumerable<long> values)
	{
		var ordered = values.Order().ToArray();
		return ordered.Length % 2 == 0 ? (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2 : ordered[ordered.Length / 2];
	}

	private sealed class EmptyHistoryReader : IProjectGitHistoryReader
	{
		public static readonly EmptyHistoryReader Instance = new();
		public Task<ProjectGitHistorySnapshot> ReadAsync(string sourceRoot, IReadOnlyList<string> candidateFiles, CancellationToken cancellationToken = default) =>
			Task.FromResult(new ProjectGitHistorySnapshot(200, 0,
				new Dictionary<string, ProjectGitFileActivity>(),
				candidateFiles.ToDictionary(static path => path, static _ => ProjectGitHistoryUnavailableReason.NotRepository),
				ProjectGitHistoryUnavailableReason.NotRepository));
	}

	private sealed class SyntheticConfigurationProvider(string root) : IDependencyConfigurationProvider
	{
		public Task<DependencyResolverConfiguration> ReadAsync(string sourceRoot, IReadOnlyList<string> manifestFiles, CancellationToken cancellationToken) =>
			Task.FromResult(new DependencyResolverConfiguration("synthetic",
				[new DependencyScopeDescriptor("synthetic", root, LanguageId.CSharp, [], null, false,
					new Dictionary<string, IReadOnlyList<string>>(), null, new HashSet<string>(), [], true)],
				new Dictionary<string, PackageMapDescriptor>(), new HashSet<string>(),
				new Dictionary<string, IReadOnlySet<string>>(), new HashSet<string>()));
	}

	private sealed class SyntheticExtractor : IDependencyFactExtractor
	{
		public int ParseCount { get; private set; }
		public int CompiledQuerySetCount => 0;
		public ValueTask<PreparedDependencySource> PrepareAsync(string sourceRoot, string fullPath,
			DependencyResolverConfiguration configuration, DependencyFactsLimits limits, CancellationToken cancellationToken,
			string? contentIdentity = null)
		{
			var relative = Path.GetRelativePath(sourceRoot, fullPath).Replace('\\', '/');
			return ValueTask.FromResult(new PreparedDependencySource(fullPath, relative, "synthetic", LanguageId.CSharp,
				relative, "synthetic", string.Empty));
		}
		public FileFacts Extract(PreparedDependencySource source, DependencyFactsLimits limits)
		{
			ParseCount++;
			var index = int.Parse(Path.GetFileNameWithoutExtension(source.RelativePath)[4..]);
			var declaration = new DeclarationFact(new SymbolIdentity("synthetic", LanguageId.CSharp, SymbolKind.Class,
				$"Type{index:D5}", 0), [new SourceSite(source.RelativePath, 1, $"Type{index:D5}")]);
			var references = index == 0 ? [] : new[] { new ReferenceFact(EvidenceLayer.TypeReference,
				$"Type{index - 1:D5}", 0, "type", new SourceSite(source.RelativePath, 1, $"Type{index - 1:D5}")) };
			return new FileFacts(source.RelativePath, "synthetic", LanguageId.CSharp, source.ContentFingerprint, 0,
				DependencyFileStatus.Supported, null, false, new Dictionary<string, int>(), [declaration], [], references,
				[], new Dictionary<string, string>(), [], new Dictionary<string, string>(), []);
		}
		public void Dispose() { }
	}

	private sealed record OperationSample(string Operation, int Repetition, double ElapsedMilliseconds, long AllocatedBytes, long PeakWorkingSetBytes);
	private sealed record OperationSummary(string Operation, double MedianMilliseconds, double MinimumMilliseconds, double MaximumMilliseconds,
		long MedianAllocatedBytes, long MinimumAllocatedBytes, long MaximumAllocatedBytes);
	private sealed record OperationReport(string Root, int Files, IReadOnlyList<OperationSample> Samples, IReadOnlyList<OperationSummary> Summaries);
}
