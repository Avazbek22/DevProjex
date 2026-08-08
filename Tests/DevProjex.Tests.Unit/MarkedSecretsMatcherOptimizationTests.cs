using System.Diagnostics;
using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class MarkedSecretsMatcherOptimizationTests(ITestOutputHelper output)
{
	private static readonly CancellationToken CancellationToken = TestContext.Current.CancellationToken;

	[Fact]
	public void PositionIndexMatcher_ReturnsTheSameFindingsAsDirectShaEnumeration()
	{
		var values = new[]
		{
			"alpha-secret-123",
			"beta_secret_4567",
			"token.with.punctuation"
		};
		var marks = values.Select(CreateMark).ToArray();
		const string content =
			"A=alpha-secret-123; ignored=xalpha-secret-123\n" +
			"B='beta_secret_4567'\r\n" +
			"C=token.with.punctuation; again=alpha-secret-123";
		var matcher = new MarkedSecretsMatcher(marks, []);

		var actual = matcher.Match("config.env", content, CancellationToken)
			.Select(ToComparableFinding)
			.OrderBy(static finding => finding.Start)
			.ThenBy(static finding => finding.Length)
			.ToArray();
		var expected = DirectShaMatch(marks, content)
			.OrderBy(static finding => finding.Start)
			.ThenBy(static finding => finding.Length)
			.ToArray();

		Assert.Equal(expected, actual);
	}

	[Fact]
	public void SimilarCandidateWithTheSameLength_DoesNotPassTheShaCheck()
	{
		const string marked = "secret-value-0001";
		const string content = "TOKEN=secret-value-0002;";
		var matcher = new MarkedSecretsMatcher([CreateMark(marked)], []);

		var findings = matcher.Match("config.env", content, CancellationToken);

		Assert.Empty(findings);
	}

	[Fact]
	public void TokenBoundaries_AcceptOnlyCompleteAdminToken()
	{
		const string marked = "admin123";
		const string content = "xadmin123 admin123x \"admin123\" =admin123;";
		var matcher = new MarkedSecretsMatcher([CreateMark(marked)], []);

		var findings = matcher.Match("config.env", content, CancellationToken);

		Assert.Equal(2, findings.Count);
		Assert.All(findings, finding => Assert.Equal(marked, finding.Value));
		Assert.Equal(
			[
				content.IndexOf("\"admin123\"", StringComparison.Ordinal) + 1,
				content.LastIndexOf("admin123", StringComparison.Ordinal)
			],
			findings.Select(static finding => finding.Start));
	}

	[Theory]
	[InlineData("abcd\nefgh")]
	[InlineData("abcd\r\nefgh")]
	public void CandidateCrossingLineBreak_IsNeverMatched(string multilineValue)
	{
		var mark = new MarkedSecretProfileEntry(
			MarkedSecretValueNormalizer.ComputeHash(multilineValue),
			Key: null,
			multilineValue.Length);
		var matcher = new MarkedSecretsMatcher([mark], []);

		var findings = matcher.Match("config.env", multilineValue, CancellationToken);

		Assert.Empty(findings);
	}

	[Fact]
	public void InvalidPersistedHashLength_IsRejected()
	{
		const string marked = "secret-value-0001";
		var invalidMark = new MarkedSecretProfileEntry("0011223344", null, marked.Length);
		var matcher = new MarkedSecretsMatcher([invalidMark], []);

		var findings = matcher.Match("config.env", marked, CancellationToken);

		Assert.Empty(findings);
	}

	[Fact]
	public void SessionMark_UsesCanonicalSourceOffsetAcrossMixedNewlines()
	{
		const string marked = "secret-value-0001";
		const string content = "first\r\nsecond\nKEY=secret-value-0001\r\nlast";
		var value = CreateValue(marked);
		var sessionMark = new SessionMarkedSecret(
			"config.env",
			content.IndexOf(marked, StringComparison.Ordinal),
			value.Length,
			value.Hash);
		var matcher = new MarkedSecretsMatcher([], [sessionMark]);

		var finding = Assert.Single(matcher.Match("config.env", content, CancellationToken));

		Assert.Equal(content.IndexOf(marked, StringComparison.Ordinal), finding.Start);
		Assert.Equal(marked, finding.Value);
	}

	[Fact]
	public void OneMegabyteWithFiveMarkedLengths_CompletesWithinRegressionBudget()
	{
		const int targetLength = 1024 * 1024;
		var sourceLine = "public static string ResolveValue(int index) => values[index] + suffix_1234567890;\n";
		var builder = new StringBuilder(targetLength + sourceLine.Length);
		while (builder.Length < targetLength)
			builder.Append(sourceLine);
		var content = builder.ToString(0, targetLength);
		var marks = new[]
		{
			CreateMark("absent-secret-01"),
			CreateMark("absent-secret-value-002"),
			CreateMark("absent-secret-value-number-0003"),
			CreateMark("absent-secret-value-number-with-suffix-0004"),
			CreateMark("absent-secret-value-number-with-longer-suffix-00005")
		};
		var matcher = new MarkedSecretsMatcher(marks, []);
		_ = matcher.Match("warmup.cs", "TOKEN=warmup-value;", CancellationToken);

		var stopwatch = Stopwatch.StartNew();
		var findings = matcher.Match("large.cs", content, CancellationToken);
		stopwatch.Stop();
		output.WriteLine(
			"MarkedSecretsMatcher: {0:N0} chars, {1} distinct lengths, {2:F1} ms",
			content.Length,
			marks.Select(static mark => mark.Length).Distinct().Count(),
			stopwatch.Elapsed.TotalMilliseconds);

		Assert.Empty(findings);
		Assert.True(
			stopwatch.Elapsed < TimeSpan.FromSeconds(10),
			$"1 MiB matching took {stopwatch.Elapsed.TotalMilliseconds:F1} ms; budget is 10000 ms.");
	}

	private static ComparableFinding[] DirectShaMatch(
		IEnumerable<MarkedSecretProfileEntry> marks,
		string content)
	{
		var findings = new List<ComparableFinding>();
		foreach (var group in marks.GroupBy(static mark => mark.Length))
		{
			for (var start = 0; start <= content.Length - group.Key; start++)
			{
				if (!SecretTokenBoundary.HasBoundaries(content, start, group.Key) ||
				    content.AsSpan(start, group.Key).IndexOfAny('\r', '\n') >= 0)
				{
					continue;
				}

				var hash = MarkedSecretValueNormalizer.ComputeHash(content.AsSpan(start, group.Key));
				if (group.Any(mark => mark.H.Equals(hash, StringComparison.OrdinalIgnoreCase)))
					findings.Add(new ComparableFinding(start, group.Key, content.Substring(start, group.Key)));
			}
		}

		return findings.ToArray();
	}

	private static ComparableFinding ToComparableFinding(DetectedSecret finding) =>
		new(finding.Start, finding.Length, finding.Value);

	private static MarkedSecretProfileEntry CreateMark(string value)
	{
		var normalized = CreateValue(value);
		return new MarkedSecretProfileEntry(normalized.Hash, null, normalized.Length);
	}

	private static MarkedSecretValue CreateValue(string value)
	{
		Assert.True(MarkedSecretValueNormalizer.TryCreate(value, out var result, out var error), error.ToString());
		return result;
	}

	private sealed record ComparableFinding(int Start, int Length, string Value);
}
