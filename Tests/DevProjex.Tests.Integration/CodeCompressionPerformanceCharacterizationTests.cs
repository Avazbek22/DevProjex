using System.Diagnostics;
using DevProjex.Application.Compression;
using DevProjex.Application.Services;
using DevProjex.Infrastructure.Compression;

namespace DevProjex.Tests.Integration;

[Trait("Category", "LocalPerformance")]
public sealed class CodeCompressionPerformanceCharacterizationTests
{
	[Fact(Timeout = 30_000)]
	public async Task MixedStackWarmup_MakesRepeatedPreviewAnalysisFree()
	{
		using var workspace = new TemporaryDirectory();
		var paths = CreateMixedStack(workspace, copiesPerLanguage: 12);
		using var coldSession = CodeCompressionFactory.CreateSession();
		var coldPreview = await MeasurePreviewAsync(workspace.Path, paths, coldSession);
		using var session = CodeCompressionFactory.CreateSession();
		var context = new CodeCompressionContext(workspace.Path, session);
		var prewarmer = new CodeCompressionPrewarmer(new FileContentAnalyzer());

		var warmup = await prewarmer.WarmAsync(
			context,
			paths,
			TestContext.Current.CancellationToken);
		var afterWarmup = session.Diagnostics;
		var firstPreview = await MeasurePreviewAsync(workspace.Path, paths, session);
		var afterFirstPreview = session.Diagnostics;
		var repeatedPreview = await MeasurePreviewAsync(workspace.Path, paths, session);
		var afterRepeatedPreview = session.Diagnostics;

		Assert.Equal(paths.Count, warmup.WarmedFiles);
		Assert.Equal(paths.Count, afterWarmup.AnalysisExecutions);
		Assert.Equal(afterWarmup.AnalysisExecutions, afterFirstPreview.AnalysisExecutions);
		Assert.Equal(afterWarmup.AnalysisExecutions, afterRepeatedPreview.AnalysisExecutions);
		Assert.Equal(paths.Count * 2, afterRepeatedPreview.CacheHits);
		Assert.Equal(coldPreview.CharacterCount, firstPreview.CharacterCount);
		Assert.Equal(firstPreview.CharacterCount, repeatedPreview.CharacterCount);
		Assert.True(
			repeatedPreview.Elapsed < TimeSpan.FromSeconds(5),
			$"Warm Preview took {repeatedPreview.Elapsed.TotalMilliseconds:F2} ms.");
		WriteMeasurements(
			"mixed-stack",
			paths.Count,
			coldPreview,
			warmup,
			firstPreview,
			repeatedPreview,
			afterRepeatedPreview);
	}

	[Fact(Timeout = 120_000)]
	public async Task RealProject_ReportsColdAndWarmPreviewCosts()
	{
		var root = Environment.GetEnvironmentVariable("DEVPROJEX_COMPRESSION_PROFILE_ROOT");
		if (string.IsNullOrWhiteSpace(root))
			Assert.Skip("Set DEVPROJEX_COMPRESSION_PROFILE_ROOT to profile a real project.");

		root = Path.GetFullPath(root);
		using var coldSession = CodeCompressionFactory.CreateSession();
		var coldPaths = EnumerateSupportedProjectFiles(root, coldSession);
		Assert.NotEmpty(coldPaths);
		var coldPreview = await MeasurePreviewAsync(root, coldPaths, coldSession);
		using var session = CodeCompressionFactory.CreateSession();
		var paths = EnumerateSupportedProjectFiles(root, session);
		Assert.NotEmpty(paths);
		var context = new CodeCompressionContext(root, session);
		var warmup = await new CodeCompressionPrewarmer(new FileContentAnalyzer()).WarmAsync(
			context,
			paths,
			TestContext.Current.CancellationToken);
		var firstPreview = await MeasurePreviewAsync(root, paths, session);
		var repeatedPreview = await MeasurePreviewAsync(root, paths, session);
		var diagnostics = session.Diagnostics;

		Assert.Equal(warmup.WarmedFiles, diagnostics.AnalysisExecutions);
		Assert.Equal(coldPreview.CharacterCount, firstPreview.CharacterCount);
		Assert.Equal(firstPreview.CharacterCount, repeatedPreview.CharacterCount);
		WriteMeasurements(
			Path.GetFileName(root),
			paths.Count,
			coldPreview,
			warmup,
			firstPreview,
			repeatedPreview,
			diagnostics);
	}

	private static async Task<PreviewMeasurement> MeasurePreviewAsync(
		string root,
		IReadOnlyList<string> paths,
		CodeCompressionSession session)
	{
		var stopwatch = Stopwatch.StartNew();
		using var document = await new PreviewDocumentBuilder(new FileContentAnalyzer())
			.BuildContentDocumentAsync(
				paths,
				TestContext.Current.CancellationToken,
				path => Path.GetRelativePath(root, path),
				transformationContext: ContentTransformationContext.For(
					new CodeCompressionContext(root, session),
					redaction: null));
		stopwatch.Stop();
		Assert.NotNull(document);
		return new PreviewMeasurement(stopwatch.Elapsed, document.CharacterCount, document.LineCount);
	}

	private static IReadOnlyList<string> EnumerateSupportedProjectFiles(
		string root,
		CodeCompressionSession session) =>
		Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
			.Where(path => !ContainsExcludedDirectory(root, path))
			.Where(path => session.IsSupported(path))
			.Where(path => TryGetLength(path, out var length) &&
			               length <= TreeSitterCodeCompressor.MaximumParsableCharacters)
			.Order(PathComparer.Default)
			.ToArray();

	private static bool ContainsExcludedDirectory(string root, string path)
	{
		var relative = Path.GetRelativePath(root, path);
		var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		return segments.Take(segments.Length - 1).Any(static segment =>
			segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
			segment.Equals(".idea", StringComparison.OrdinalIgnoreCase) ||
			segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
			segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
			segment.Equals("artifacts", StringComparison.OrdinalIgnoreCase));
	}

	private static bool TryGetLength(string path, out long length)
	{
		try
		{
			length = new FileInfo(path).Length;
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			length = 0;
			return false;
		}
	}

	private static IReadOnlyList<string> CreateMixedStack(
		TemporaryDirectory workspace,
		int copiesPerLanguage)
	{
		var sources = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["c"] = "int add(int a, int b) { int value = a + b; value += 10; return value; }",
			["cpp"] = "int add(int a, int b) { int value = a + b; value += 10; return value; }",
			["cs"] = "sealed class Sample { int Add(int a, int b) { var value = a + b; value += 10; return value; } }",
			["go"] = "package sample\nfunc add(a int, b int) int { value := a + b; value += 10; return value }",
			["java"] = "final class Sample { int add(int a, int b) { int value = a + b; value += 10; return value; } }",
			["js"] = "export function add(a, b) { let value = a + b; value += 10; return value; }",
			["py"] = "def add(a, b):\n    value = a + b\n    value += 10\n    return value\n",
			["rs"] = "fn add(a: i32, b: i32) -> i32 { let mut value = a + b; value += 10; value }",
			["ts"] = "export function add(a: number, b: number): number { let value = a + b; value += 10; return value; }",
			["tsx"] = "export function Sample() { const value = 42; return <section>{value + 10}</section>; }"
		};
		var paths = new List<string>(sources.Count * copiesPerLanguage);
		foreach (var (extension, source) in sources)
		{
			for (var index = 0; index < copiesPerLanguage; index++)
				paths.Add(workspace.CreateFile($"{extension}/sample-{index:D3}.{extension}", source));
		}
		paths.Sort(PathComparer.Default);
		return paths;
	}

	private static void WriteMeasurements(
		string name,
		int files,
		PreviewMeasurement coldPreview,
		CodeCompressionWarmupResult warmup,
		PreviewMeasurement firstPreview,
		PreviewMeasurement repeatedPreview,
		CodeCompressionDiagnosticsSnapshot diagnostics) =>
		TestContext.Current.TestOutputHelper?.WriteLine(
			$"Code compression {name}: files={files:N0}, " +
			$"coldPreview={coldPreview.Elapsed.TotalMilliseconds:F2} ms, " +
			$"prewarm={warmup.Elapsed.TotalMilliseconds:F2} ms, " +
			$"firstPreview={firstPreview.Elapsed.TotalMilliseconds:F2} ms, " +
			$"warmPreview={repeatedPreview.Elapsed.TotalMilliseconds:F2} ms, " +
			$"chars={firstPreview.CharacterCount:N0}, lines={firstPreview.LineCount:N0}, " +
			$"hashes={diagnostics.HashComputations:N0}, analyses={diagnostics.AnalysisExecutions:N0}, " +
			$"cacheHits={diagnostics.CacheHits:N0}, prewarmReuses={diagnostics.PrewarmReuses:N0}.");

	private readonly record struct PreviewMeasurement(
		TimeSpan Elapsed,
		long CharacterCount,
		int LineCount);
}
