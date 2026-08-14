using System.Diagnostics;
using System.Runtime.InteropServices;
using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.Secrets;

namespace DevProjex.Tests.Unit;

[Trait("Category", "LocalPerformance")]
public sealed class GitleaksSecretDetectorAcceptanceBenchmarkTests
{
	private const string EnabledVariable = "DEVPROJEX_RUN_SECRET_INSPECTION_BENCHMARK";
	private const string CorpusResourceSuffix = ".Fixtures.Secrets.gitleaks-v8.30.1-corpus.jsonl";
	private const int MeasuredRuns = 3;
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true
	};

	[Fact(Timeout = 900_000)]
	public async Task PinnedCorpus_RecordsColdAndWarmAcceptanceDistribution()
	{
		if (!string.Equals(Environment.GetEnvironmentVariable(EnabledVariable), "1", StringComparison.Ordinal))
			Assert.Skip($"Set {EnabledVariable}=1 to run the detector acceptance benchmark.");

		var cases = LoadCorpus();
		var runs = new List<AcceptanceRun>(MeasuredRuns);
		for (var run = 0; run < MeasuredRuns; run++)
		{
			var detector = new GitleaksSecretDetector();
			var cold = Measure(detector, cases);
			var warm = Measure(detector, cases);
			runs.Add(new AcceptanceRun(run + 1, cold, warm));
		}

		var report = new AcceptanceReport(
			SchemaVersion: 1,
			CreatedAtUtc: DateTimeOffset.UtcNow,
			OperatingSystem: RuntimeInformation.OSDescription,
			Framework: RuntimeInformation.FrameworkDescription,
			ProcessorArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
			CaseCount: cases.Count,
			Runs: runs,
			Cold: Aggregate(runs.SelectMany(static run => run.Cold.FileMilliseconds)),
			Warm: Aggregate(runs.SelectMany(static run => run.Warm.FileMilliseconds)));
		var outputPath = Path.Combine(
			FindRepositoryRoot(),
			"artifacts",
			"secret-inspection",
			$"gitleaks-acceptance-{GetOsMoniker()}.json");
		Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
		await File.WriteAllTextAsync(
			outputPath,
			JsonSerializer.Serialize(report, JsonOptions),
			TestContext.Current.CancellationToken);

		Assert.All(runs, static run => Assert.Equal(run.Cold.Fingerprint, run.Warm.Fingerprint));
	}

	private static AcceptancePass Measure(
		GitleaksSecretDetector detector,
		IReadOnlyList<CorpusCase> cases)
	{
		var timings = new double[cases.Count];
		var fingerprint = new HashCode();
		var total = Stopwatch.StartNew();
		for (var index = 0; index < cases.Count; index++)
		{
			var testCase = cases[index];
			var started = Stopwatch.GetTimestamp();
			var findings = detector.Detect(
				testCase.Path,
				testCase.Content,
				new SecretFileInspectionBudget(),
				TestContext.Current.CancellationToken);
			timings[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
			fingerprint.Add(testCase.RuleId, StringComparer.Ordinal);
			fingerprint.Add(testCase.ShouldMatch);
			foreach (var finding in findings)
			{
				fingerprint.Add(finding.RuleId, StringComparer.Ordinal);
				fingerprint.Add(finding.Start);
				fingerprint.Add(finding.Length);
			}
		}
		total.Stop();
		return new AcceptancePass(total.Elapsed.TotalMilliseconds, timings, fingerprint.ToHashCode());
	}

	private static Percentiles Aggregate(IEnumerable<double> samples)
	{
		var ordered = samples.Order().ToArray();
		return new Percentiles(
			MedianMilliseconds: Percentile(ordered, 0.50),
			P95Milliseconds: Percentile(ordered, 0.95),
			P99Milliseconds: Percentile(ordered, 0.99),
			MaximumMilliseconds: ordered[^1]);
	}

	private static double Percentile(IReadOnlyList<double> ordered, double percentile)
	{
		var index = (int)Math.Ceiling(percentile * ordered.Count) - 1;
		return ordered[Math.Clamp(index, 0, ordered.Count - 1)];
	}

	private static IReadOnlyList<CorpusCase> LoadCorpus()
	{
		var assembly = typeof(GitleaksSecretDetectorAcceptanceBenchmarkTests).Assembly;
		var resourceName = assembly.GetManifestResourceNames()
			.Single(name => name.EndsWith(CorpusResourceSuffix, StringComparison.Ordinal));
		using var stream = assembly.GetManifestResourceStream(resourceName) ??
		                   throw new InvalidDataException("The embedded Gitleaks corpus is unavailable.");
		using var reader = new StreamReader(stream, Encoding.UTF8);
		var cases = new List<CorpusCase>();
		while (reader.ReadLine() is { } line)
		{
			var encoded = JsonSerializer.Deserialize<EncodedCorpusCase>(line, JsonOptions) ??
			              throw new InvalidDataException("The embedded Gitleaks corpus contains an invalid row.");
			var contentBase64 = encoded.ContentBase64 ??
			                    (encoded.ContentBase64Parts is { Length: > 0 }
				                    ? string.Concat(encoded.ContentBase64Parts)
				                    : throw new InvalidDataException("The corpus case has no encoded content."));
			cases.Add(new CorpusCase(
				encoded.RuleId,
				encoded.Path,
				Encoding.UTF8.GetString(Convert.FromBase64String(contentBase64)),
				encoded.ShouldMatch));
		}
		return cases;
	}

	private static string FindRepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DevProjex.sln")))
			directory = directory.Parent;
		return directory?.FullName ?? throw new DirectoryNotFoundException("The repository root was not found.");
	}

	private static string GetOsMoniker() =>
		OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

	private sealed record EncodedCorpusCase(
		string RuleId,
		string Path,
		string? ContentBase64,
		string[]? ContentBase64Parts,
		bool ShouldMatch);

	private sealed record CorpusCase(string RuleId, string Path, string Content, bool ShouldMatch);
	private sealed record AcceptancePass(double TotalMilliseconds, double[] FileMilliseconds, int Fingerprint);
	private sealed record AcceptanceRun(int Run, AcceptancePass Cold, AcceptancePass Warm);
	private sealed record Percentiles(
		double MedianMilliseconds,
		double P95Milliseconds,
		double P99Milliseconds,
		double MaximumMilliseconds);
	private sealed record AcceptanceReport(
		int SchemaVersion,
		DateTimeOffset CreatedAtUtc,
		string OperatingSystem,
		string Framework,
		string ProcessorArchitecture,
		int CaseCount,
		IReadOnlyList<AcceptanceRun> Runs,
		Percentiles Cold,
		Percentiles Warm);
}
