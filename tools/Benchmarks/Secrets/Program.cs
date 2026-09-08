using System.Security.Cryptography;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using DevProjex.Application.Diagnostics;
using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.Secrets;

if (args is ["--verify"])
{
	BenchmarkVerification.Write();
	return;
}

if (args is ["--diagnostics"])
{
	BenchmarkVerification.WriteDiagnostics();
	return;
}

BenchmarkSwitcher.FromAssembly(typeof(SecretDetectorBenchmarks).Assembly).Run(args);

[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class SecretDetectorBenchmarks
{
	private readonly GitleaksSecretDetector _detector = new();
	private string _content = string.Empty;

	[Params("clean", "rejected-noise", "findings")]
	public string Corpus { get; set; } = string.Empty;

	[GlobalSetup]
	public void Setup()
	{
		_content = BenchmarkCorpora.Create(Corpus);
		_detector.WarmUp();
	}

	[Benchmark]
	public int Detect() => _detector.Detect(
		"src/sample.cs",
		_content,
		new SecretFileInspectionBudget()).Count;
}

internal static class BenchmarkCorpora
{
	public static string Create(string corpus) => corpus switch
	{
		"clean" => string.Concat(Enumerable.Repeat(
			"public sealed class Widget { public int Value { get; init; } }\n",
			4_000)),
		"rejected-noise" => string.Concat(Enumerable.Range(0, 4_000).Select(
			static index => $"password_{index} = \"aaaaaaaaaaaaaaaaaaaaaaaa\";\n")),
		"findings" => string.Concat(Enumerable.Range(0, 100).Select(
			static index => $"api_key_{index} = \"A7d9mQ2xK4vN8sR6tY3uW5zB1cE0fG2h\";\n")),
		_ => throw new ArgumentOutOfRangeException(nameof(corpus))
	};
}

internal static class BenchmarkVerification
{
	public static void Write()
	{
		var detector = new GitleaksSecretDetector();
		detector.WarmUp();
		foreach (var corpus in new[] { "clean", "rejected-noise", "findings" })
		{
			var findings = detector.Detect("src/sample.cs", BenchmarkCorpora.Create(corpus));
			using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
			foreach (var finding in findings)
			{
				Append(hash, finding.RuleId);
				Append(hash, finding.Start.ToString(System.Globalization.CultureInfo.InvariantCulture));
				Append(hash, finding.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
				Append(hash, finding.Value);
			}
			Console.WriteLine($"{corpus}: findings={findings.Count}; sha256={Convert.ToHexString(hash.GetHashAndReset())}");
		}
	}

	public static void WriteDiagnostics()
	{
		var detector = new GitleaksSecretDetector();
		detector.WarmUp();
		foreach (var corpus in new[] { "clean", "rejected-noise", "findings" })
		{
			using var measurement = ContentPipelineDiagnostics.BeginMeasurement();
			var findings = detector.Detect("src/sample.cs", BenchmarkCorpora.Create(corpus));
			var diagnostics = measurement.Capture();
			Console.WriteLine(
				$"{corpus}: findings={findings.Count}; secondary-regex={diagnostics.SecondarySecretRegexRuns}; " +
				$"rejected-line-context={diagnostics.RejectedMatchLineContexts}; " +
				$"line-index={diagnostics.LineIndexBuilds}");
		}
	}

	private static void Append(IncrementalHash hash, string value)
	{
		hash.AppendData(Encoding.UTF8.GetBytes(value));
		hash.AppendData([0]);
	}
}

[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class LineRangeIndexBenchmarks
{
	private string _content = string.Empty;
	private int[] _offsets = [];

	[Params("lf", "crlf", "mixed")]
	public string Newlines { get; set; } = string.Empty;

	[GlobalSetup]
	public void Setup()
	{
		var separator = Newlines switch
		{
			"lf" => "\n",
			"crlf" => "\r\n",
			"mixed" => "\r\n",
			_ => throw new ArgumentOutOfRangeException()
		};
		_content = string.Join(separator, Enumerable.Range(0, 20_000).Select(
			static index => $"line-{index:D5}-abcdefghijklmnopqrstuvwxyz"));
		if (Newlines == "mixed")
			_content = _content.Replace("\r\nline-00010", "\rline-00010", StringComparison.Ordinal)
				.Replace("\r\nline-00020", "\nline-00020", StringComparison.Ordinal);
		_offsets = Enumerable.Range(0, 2_000)
			.Select(index => index * (_content.Length / 2_000))
			.ToArray();
	}

	[Benchmark]
	public long ResolveLines()
	{
		var index = new GitleaksSecretDetector.LineRangeIndex(_content);
		long checksum = 0;
		foreach (var offset in _offsets)
		{
			var range = index.GetContainingLine(offset, 1);
			checksum += range.Start + range.Length;
		}
		return checksum;
	}

	[Benchmark]
	public int BuildIndex()
	{
		var index = new GitleaksSecretDetector.LineRangeIndex(_content);
		return index.GetContainingLine(_content.Length - 1, 1).Start;
	}
}
