using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class GitleaksSecretDetectorTests
{
	private static readonly GitleaksSecretDetector Detector = new();
	private static readonly ShapeGateAuditRow[] ShapeGateAudit =
	[
		new("databricks-api-token", "dapi + 32 lower hex, optional -digit, value terminator", "dapi + exactly 32 ASCII hex", "weaker", false),
		new("gocardless-api-token", "case-insensitive live_ + 40 [a-z0-9-_=]", "case-insensitive live_ + exactly 40 ASCII word/-/=", "equal-or-weaker", true),
		new("harness-api-key", "pat./sat. with exact 22.24.20 ASCII segments", "same prefixes and segment lengths/classes", "equal", false),
		new("intra42-client-secret", "case-sensitive s-s4t2ud-/af- + 64 case-insensitive hex", "same prefixes + exactly 64 ASCII hex", "equal-or-weaker", false),
		new("lob-api-key", "case-insensitive live_/test_ + 35 hex", "same prefixes + 35 ASCII hex", "equal-or-weaker", false),
		new("1password-secret-key", "bounded A3- segmented uppercase token", "same two segment layouts and word boundaries", "equal", false),
		new("1password-service-account-token", "ops_eyJ + at least 250 base64 and up to three =", "ops_ + at least 250 base64", "weaker", false),
		new("cohere-api-token", "provider context + exactly 40 ASCII alphanumeric", "any run of 40 ASCII alphanumeric", "weaker", false),
		new("lob-pub-api-key", "provider context + test_pub_/live_pub_ + 31 hex", "case-insensitive prefix only", "weaker", false),
		new("sendgrid-api-token", "SG. + exactly 66 case-insensitive [a-z0-9=_-.] + terminator", "SG. + exactly 66 of the same ASCII class", "weaker", false),
		new("sentry-access-token", "provider context + exactly 64 case-insensitive hex", "any run of 64 ASCII hex", "weaker", false),
		new("square-access-token", "EAAA/sq0atp- + 22..60 Unicode word or hyphen + terminator", "same prefixes, lengths and Unicode word/hyphen class", "equal-or-weaker", true),
		new("telegram-bot-api-token", "5..16 digits + :A + 34 case-insensitive ASCII word/hyphen", "same numeric bounds, uppercase A, and suffix class", "equal-or-weaker", false),
		new("twitter-access-secret", "provider context + exactly 45 case-insensitive alphanumeric", "any run of 45 ASCII alphanumeric", "weaker", false),
		new("twitter-access-token", "15..25 digits + - + 20..40 alphanumeric + terminator", "same component bounds without requiring context/terminator", "weaker", false),
		new("twitter-api-key", "provider context + exactly 25 case-insensitive alphanumeric", "any run of 25 ASCII alphanumeric", "weaker", false),
		new("twitter-api-secret", "provider context + exactly 50 case-insensitive alphanumeric", "any run of 50 ASCII alphanumeric", "weaker", false),
		new("twitter-bearer-token", "case-insensitive A{22} + 80..100 alphanumeric/% + terminator", "case-insensitive 22-A prefix + same run bounds/class", "equal-or-weaker", true),
		new("vault-service-token", "hvs. + 90..120 Unicode word/hyphen, or case-insensitive s. + 24 alphanumeric", "same alternatives, lengths and character classes", "equal-or-weaker", true),
		new("twilio-api-key", "SK + any following 32 ASCII hex (no right boundary)", "SK + at least 32 ASCII hex", "equal-or-weaker", true),
		new("jwt", "three bounded ey... JWT segments and terminator", "case-sensitive ey... .ey prefix evidence", "weaker", false),
		new("yandex-access-token", "case-insensitive t1. structured token in provider context", "case-insensitive t1. prefix", "weaker", true),
		new("yandex-api-key", "case-insensitive AQVN + 35..38 ASCII word/hyphen", "same prefix, bounds and class", "equal-or-weaker", false),
		new("yandex-aws-access-token", "case-insensitive YC + exactly 38 ASCII word/hyphen", "same prefix, length and class", "equal-or-weaker", false)
	];
	private static readonly string[] SupplementalPositiveRuleIds =
	[
		"dropbox-long-lived-api-token",
		"dropbox-short-lived-api-token"
	];
	private const string CorpusResourceSuffix = ".Fixtures.Secrets.gitleaks-v8.30.1-corpus.jsonl";
	// This storage contract scans all 302 encoded fixtures as one synthetic file; it is not a
	// production per-file deadline assertion and needs headroom under parallel CI load.
	private static readonly TimeSpan EncodedCorpusInspectionTimeout = TimeSpan.FromSeconds(30);
	private static readonly JsonSerializerOptions CorpusJsonOptions = new()
	{
		PropertyNameCaseInsensitive = true
	};

	[Fact]
	public void EmbeddedConfiguration_LoadsEveryPinnedRule()
	{
		Assert.Equal(GitleaksSecretDetector.ExpectedRuleCount, Detector.RuleCount);
	}

	[Fact]
	public void KeywordPrefilter_MatchesTheLinearCandidateOracleAcrossPinnedCasesAndUnicodeCaseFolding()
	{
		var cases = LoadUpstreamCorpus()
			.Select(static item => (item.Path, item.Content))
			.Append(("src/overlap.txt", "GITHUB github_pat TOKEN api KEY aws \u212Aey"));

		foreach (var (path, content) in cases)
		{
			var expected = Detector.InspectCandidateRuleIdsByLinearSearch(
				path,
				content.AsSpan(),
				TestContext.Current.CancellationToken);
			var actual = Detector.InspectCandidateRuleIds(
				path,
				content.AsSpan(),
				TestContext.Current.CancellationToken);

			Assert.Equal(expected, actual);
		}
	}

	[Fact]
	public void ShapeGateAudit_CoversTheTwentyFourPinnedValueShapeRules()
	{
		Assert.Equal(24, ShapeGateAudit.Length);
		Assert.Equal(24, ShapeGateAudit.Select(static row => row.RuleId).Distinct(StringComparer.Ordinal).Count());
		Assert.Equal(6, ShapeGateAudit.Count(static row => row.Corrected));
		Assert.All(ShapeGateAudit, static row =>
		{
			Assert.NotEmpty(row.RegexRequirement);
			Assert.NotEmpty(row.GateGuarantee);
			Assert.Contains(row.Verdict, new[] { "equal", "weaker", "equal-or-weaker" });
		});
	}

	[Fact(Timeout = 30_000)]
	public void ShapeGates_AcceptEveryPinnedOrGeneratedTextThatMatchesItsRegex()
	{
		// The pinned upstream corpus is the pre-audit sample set. Every generated case below
		// is accepted only when the current exact regex confirms that it is still positive.
		var pinnedCases = LoadUpstreamCorpus()
			.Where(static item => item.ShouldMatch)
			.ToLookup(static item => item.RuleId, StringComparer.Ordinal);
		var failures = new List<string>();

		foreach (var audit in ShapeGateAudit)
		{
			var priorSamples = pinnedCases[audit.RuleId].ToArray();
			Assert.NotEmpty(priorSamples);
			foreach (var sample in priorSamples)
			{
				Verify(sample.Content, "pinned");
				var match = Detector.InspectRuleMatch(audit.RuleId, sample.Content);
				Assert.True(match.IsMatch, $"The pinned sample no longer matches '{audit.RuleId}'.");
				foreach (var variant in GenerateRegexGuardVariants(sample.Content, match))
					Verify(variant, "generated");
			}

			void Verify(string content, string source)
			{
				if (!Detector.InspectRuleMatch(audit.RuleId, content).IsMatch ||
				    Detector.InspectRuleSpecificEvidence(audit.RuleId, content.AsSpan()))
				{
					return;
				}

				failures.Add($"{audit.RuleId}: {source} regex match was rejected by its shape gate");
			}
		}

		Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
	}

	[Fact(Timeout = 30_000)]
	public void KeywordPrefilter_MatchesLinearOracleAcrossCaseAndSeparatorVariants()
	{
		var separatorContexts = new[]
		{
			(string.Empty, string.Empty), (" ", " "), ("\t", "\t"), ("\r", "\n"),
			("=", ";"), (":", ","), ("/", "/"), (".", "."), ("_", "_"), ("-", "-")
		};
		foreach (var ruleId in Detector.InspectRuleIds())
		{
			foreach (var keyword in Detector.InspectRuleKeywords(ruleId))
			{
				foreach (var spelling in GenerateCaseVariants(keyword))
				foreach (var (before, after) in separatorContexts)
				{
					var content = string.Concat(before, spelling, after);
					var expected = Detector.InspectCandidateRuleIdsByLinearSearch(
						"src/keyword.txt",
						content.AsSpan(),
						TestContext.Current.CancellationToken);
					var actual = Detector.InspectCandidateRuleIds(
						"src/keyword.txt",
						content.AsSpan(),
						TestContext.Current.CancellationToken);

					Assert.Equal(expected, actual);
				}
			}
		}
	}

	[Fact]
	public void KeywordPrefilter_HybridStorageStaysBelowDenseAlternatives()
	{
		var statistics = Detector.InspectKeywordPrefilterStatistics();

		Assert.True(statistics.NodeCount > 1_000);
		Assert.True(statistics.TransitionCount >= statistics.NodeCount - 1);
		Assert.InRange(statistics.AlphabetSize, 1, 128);
		Assert.True(statistics.EstimatedStorageBytes < statistics.DenseAlphabetStorageBytes);
		Assert.True(statistics.DenseAlphabetStorageBytes < statistics.DenseUnicodeStorageBytes);
	}

	[Fact]
	public void Detect_BundledConfigurationPath_DoesNotReportRuleExamplesAsSecrets()
	{
		var ruleExample = "bedrock-api-" + "key-YmVkcm9jay5hbWF6b25hd3MuY29t";

		Assert.Empty(Detector.Detect(
			"Infrastructure/Secrets/Rules/gitleaks-v8.30.1.toml",
			ruleExample,
			TestContext.Current.CancellationToken));
	}

	[Fact]
	public void EmbeddedCorpus_StoresPayloadsEncodedAtRest()
	{
		var corpus = ReadUpstreamCorpusText();

		Assert.DoesNotContain("\"content\":", corpus, StringComparison.Ordinal);
		Assert.Contains("\"contentBase64\":", corpus, StringComparison.Ordinal);
		Assert.Empty(Detector.Detect(
			"Tests/Fixtures/Secrets/gitleaks-v8.30.1-corpus.jsonl",
			corpus.AsSpan(),
			new SecretFileInspectionBudget(EncodedCorpusInspectionTimeout),
			TestContext.Current.CancellationToken));
	}

	[Fact]
	public void WarmUp_ExercisesEverySelectedColdStartRule()
	{
		var detector = new GitleaksSecretDetector();

		detector.WarmUp(TestContext.Current.CancellationToken);
	}

	[Theory]
	[InlineData("AKIA" + "Z7M3Q5X2P6N4R7T5", "aws-access-token")]
	[InlineData("ghp_" + "a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL", "github-pat")]
	[InlineData("telegramBotToken = \"123456789:" + "Aa7d9_mq2xk4vn8sr6ty3uw5zb1ce0fg2hx\"", "telegram-bot-api-token")]
	public void Detect_RepresentativeUpstreamPatterns_ReturnsTypedValue(
		string content,
		string expectedRuleId)
	{
		var findings = Detector.Detect("src/config.cs", content, TestContext.Current.CancellationToken);

		var finding = Assert.Single(findings, match => match.RuleId == expectedRuleId);
		Assert.Equal(finding.Value, content.Substring(finding.Start, finding.Length));
	}

	[Fact]
	public void Detect_PrivateKey_ReturnsTheEntirePemBlockInsteadOfOnlyItsHeader()
	{
		const string privateKey =
			"-----BEGIN " + "PRIVATE KEY-----\n" +
			"MIIEvQIBADANBgkq" + "hkiG9w0BAQEFAASC" +
			"BKcwggSjAgEAAoIB" + "AQDAC4AWkdwKYSd8\n" +
			"Ks14IReLcYgA" + "DhoXk56ZzXI=\n" +
			"-----END " + "PRIVATE KEY-----";
		var content = $"before\n{privateKey}\nafter";

		var finding = Assert.Single(
			Detector.Detect("secrets/private.pem", content, TestContext.Current.CancellationToken),
			static match => match.RuleId == "private-key");

		Assert.Equal(privateKey, finding.Value);
		Assert.Equal(content.IndexOf(privateKey, StringComparison.Ordinal), finding.Start);
		Assert.Equal(privateKey.Length, finding.Length);
	}

	[Fact]
	public void Detect_PemMarkerMentionsSeparatedByCode_DoNotSpanIntoOnePrivateKeyFinding()
	{
		// A test or documentation file may mention PEM markers several times without containing a
		// key. The unbounded upstream pattern bridges such mentions across the source code between
		// them and reports everything as one giant secret; the bounded override must not.
		const string content =
			"const string fixture = \"-----BEGIN PRIVATE KEY-----\\nabc\\n-----END PRIVATE KEY-----\";\n" +
			"var start = text.IndexOf(\"-----BEGIN PRIVATE KEY-----\", StringComparison.Ordinal);\n" +
			"var length = \"-----BEGIN PRIVATE KEY-----\\nabc\\n-----END PRIVATE KEY-----\".Length;\n";

		Assert.DoesNotContain(
			Detector.Detect("src/PemFixtureTests.cs", content, TestContext.Current.CancellationToken),
			static finding => finding.RuleId == "private-key");
	}

	[Fact]
	public void Detect_PrivateKeyEmbeddedAsEscapedStringLiteral_IsStillDetected()
	{
		const string escapedKey =
			"-----BEGIN " + "PRIVATE KEY-----\\n" +
			"MIIEvQIBADANBgkq" + "hkiG9w0BAQEFAASC" +
			"BKcwggSjAgEAAoIB" + "AQDAC4AWkdwKYSd8\\n" +
			"Ks14IReLcYgA" + "DhoXk56ZzXI=\\n" +
			"-----END " + "PRIVATE KEY-----";
		var content = $"const string Key = \"{escapedKey}\";";

		var finding = Assert.Single(
			Detector.Detect("src/Keys.cs", content, TestContext.Current.CancellationToken),
			static match => match.RuleId == "private-key");

		Assert.Equal(escapedKey, finding.Value);
	}

	[Fact]
	public void Detect_ContentRulesWithoutUpstreamGeneratedCases_AreCoveredExplicitly()
	{
		var longLivedToken = "abcdefghijk" + "AAAAAAAAAA" + new string('b', 43);
		var shortLivedToken = "sl." + new string('c', 135);

		var longLivedFinding = Assert.Single(
			Detector.Detect(
				"src/dropbox.env",
				$"dropbox = \"{longLivedToken}\"",
				TestContext.Current.CancellationToken),
			static finding => finding.RuleId == "dropbox-long-lived-api-token");
		var shortLivedFinding = Assert.Single(
			Detector.Detect(
				"src/dropbox.env",
				$"dropbox = \"{shortLivedToken}\"",
				TestContext.Current.CancellationToken),
			static finding => finding.RuleId == "dropbox-short-lived-api-token");

		Assert.Equal(longLivedToken, longLivedFinding.Value);
		Assert.Equal(shortLivedToken, shortLivedFinding.Value);
	}

	[Theory]
	[InlineData("apiKey = \"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\"")]
	[InlineData("apiKey = \"example-key-do-not-use\"")]
	[InlineData("password = \"changemechangeme\"")]
	public void Detect_LowEntropyOrAllowlistedExamples_DoesNotReportGenericSecret(string content)
	{
		var findings = Detector.Detect("README.md", content, TestContext.Current.CancellationToken);

		Assert.DoesNotContain(findings, match => match.RuleId == "generic-api-key");
	}

	[Theory]
	[InlineData("", 0d)]
	[InlineData("aaaa", 0d)]
	[InlineData("abcd", 2d)]
	[InlineData("\u0430\u0431\u0430\u0431", 1d)]
	public void ShannonEntropy_PreservesAsciiAndUnicodeResults(string value, double expected)
	{
		Assert.Equal(expected, GitleaksSecretDetector.CalculateShannonEntropy(value), precision: 12);
	}

	[Fact]
	public void Detect_UserSecretsIdFastAllowlist_PreservesARealGenericFindingInTheSameFile()
	{
		const string genericValue = "A7d9mQ2xK4vN8sR6tY3uW5zB1cE0fG2h";
		const string content =
			"<UserSecretsId>ce4e9e0f-5100-41c3-89f8-35d17f41e32b</UserSecretsId>\n" +
			"apiKey = \"" + genericValue + "\"";

		var finding = Assert.Single(
			Detector.Detect("src/project.csproj", content, TestContext.Current.CancellationToken),
			static match => match.RuleId == "generic-api-key");

		Assert.Equal(genericValue, finding.Value);
	}

	[Theory]
	[InlineData("credential")]
	[InlineData("creds")]
	public void Detect_GenericFastGatePreservesEveryPinnedKeyVocabulary(string key)
	{
		const string genericValue = "A7d9mQ2xK4vN8sR6tY3uW5zB1cE0fG2h";

		var finding = Assert.Single(
			Detector.Detect(
				"src/config.txt",
				$"{key} = \"{genericValue}\"",
				TestContext.Current.CancellationToken),
			static match => match.RuleId == "generic-api-key");

		Assert.Equal(genericValue, finding.Value);
	}

	[Fact]
	public void Detect_GitleaksAllowMarker_SuppressesFindingOnThatLine()
	{
		const string content = "const token = \"ghp_" + "a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL\"; // gitleaks:allow";

		Assert.Empty(Detector.Detect("src/example.cs", content, TestContext.Current.CancellationToken));
	}

	[Fact]
	public void Detect_GitleaksAllowMarker_DoesNotSuppressFollowingCarriageReturnLine()
	{
		const string token = "ghp_" + "a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL";
		const string content = "// gitleaks:allow\rconst token = \"" + token + "\";";

		var finding = Assert.Single(
			Detector.Detect("src/example.cs", content, TestContext.Current.CancellationToken),
			static match => match.RuleId == "github-pat");

		Assert.Equal(token, finding.Value);
	}

	[Fact]
	public void Detect_GitleaksAllowMarker_AppliesToTheFullMultilineMatchContext()
	{
		const string content =
			"-----BEGIN " + "PRIVATE KEY-----\n" +
			"MIIEvQIBADANBgkq" + "hkiG9w0BAQEFAASC" +
			"BKcwggSjAgEAAoIB" + "AQDAC4AWkdwKYSd8\n" +
			"Ks14IReLcYgA" + "DhoXk56ZzXI=\n" +
			"-----END " + "PRIVATE KEY----- // gitleaks:allow";

		Assert.DoesNotContain(
			Detector.Detect("src/key.pem", content, TestContext.Current.CancellationToken),
			static finding => finding.RuleId == "private-key");
	}

	[Fact]
	public void Detect_MultilineAllowMarkerDoesNotSuppressANestedFindingFromALaterRule()
	{
		var pulumiToken = "pul-" + string.Concat(Enumerable.Repeat("a7d9b3c5", 5));
		var content =
			"-----BEGIN " + "PRIVATE KEY-----\n" +
			"MIIEvQIBADANBgkq" + "hkiG9w0BAQEFAASCBKcwggSjAgEAAoIB\n" +
			pulumiToken + "\n" +
			"Ks14IReLcYgADhoXk56ZzXI=\n" +
			"-----END " + "PRIVATE KEY----- // gitleaks:allow";

		var findings = Detector.Detect("src/key.pem", content, TestContext.Current.CancellationToken);

		Assert.DoesNotContain(findings, static finding => finding.RuleId == "private-key");
		var nested = Assert.Single(findings, static finding => finding.RuleId == "pulumi-api-token");
		Assert.Equal(pulumiToken, nested.Value);
	}

	[Theory]
	[InlineData("\n", false)]
	[InlineData("\r", false)]
	[InlineData("\r\n", false)]
	[InlineData("\n", true)]
	[InlineData("\r", true)]
	[InlineData("\r\n", true)]
	public void LineRangeIndex_MultilineCacheNeverBecomesTheLineContextOfANestedMatch(
		string newline,
		bool exceedsIndexThreshold)
	{
		var prefix = exceedsIndexThreshold ? new string('x', 64 * 1024) + newline : string.Empty;
		var first = "outer-start";
		var nested = "apiKey=A7d9mQ2xK4vN8sR6tY3uW5zB1cE0fG2h";
		var last = "--mount=type=secret, gitleaks:allow";
		var block = first + newline + nested + newline + last;
		var content = prefix + block;
		var blockStart = prefix.Length;
		var nestedStart = content.IndexOf(nested, StringComparison.Ordinal);
		var index = new GitleaksSecretDetector.LineRangeIndex(content.AsSpan());

		var multiline = index.GetContainingLine(blockStart, block.Length);
		var nestedLine = index.GetContainingLine(nestedStart, nested.Length);

		Assert.Contains(newline, content.AsSpan(multiline.Start, multiline.Length).ToString(), StringComparison.Ordinal);
		Assert.Equal(nested, content.AsSpan(nestedLine.Start, nestedLine.Length).ToString());
		Assert.DoesNotContain("gitleaks:allow", content.AsSpan(nestedLine.Start, nestedLine.Length).ToString(), StringComparison.Ordinal);
		Assert.DoesNotContain("--mount=type=secret,", content.AsSpan(nestedLine.Start, nestedLine.Length).ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public void Detect_ManyMatchesOnOneLongLine_DoesNotCopyTheLinePerFinding()
	{
		Detector.WarmUp(TestContext.Current.CancellationToken);
		const int findingCount = 12;
		var token = "ghp_" + "a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL";
		var content = new string('x', 1024 * 1024) +
		              string.Concat(Enumerable.Range(0, findingCount).Select(index =>
			              $" token{index}=\"{token}\";")) +
		              new string('y', 1024 * 1024);
		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

		var findings = Detector.Detect(
			"src/minified.js",
			content,
			new SecretFileInspectionBudget(TimeSpan.FromSeconds(10)),
			TestContext.Current.CancellationToken);
		var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

		Assert.Equal(findingCount, findings.Count(static finding => finding.RuleId == "github-pat"));
		Assert.True(
			allocatedBytes < 8L * 1024 * 1024,
			$"Expected range-backed line contexts to allocate under 8 MiB, actual: {allocatedBytes:N0} bytes.");
	}

	[Fact]
	public void Detect_GlobalPathAllowlist_IsAppliedBeforeRules()
	{
		const string content = "const token = \"ghp_" + "a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL\";";

		Assert.False(Detector.ShouldInspectPath("fixtures/image.svg"));
		Assert.True(Detector.ShouldInspectPath("src/config.cs"));
		Assert.Empty(Detector.Detect("fixtures/image.svg", content, TestContext.Current.CancellationToken));
	}

	[Theory]
	[InlineData(false, true, true, true)]
	[InlineData(true, true, false, true)]
	[InlineData(true, true, true, false)]
	[InlineData(true, false, false, false)]
	public void IsPathSufficientForAllowlist_PreservesUpstreamTwoStageSemantics(
		bool requireAll,
		bool pathMatches,
		bool hasFindingCriteria,
		bool expected)
	{
		Assert.Equal(
			expected,
			GitleaksSecretDetector.IsPathSufficientForAllowlist(
				requireAll,
				pathMatches,
				hasFindingCriteria));
	}

	[Fact]
	public void PortedValueRules_MatchPinnedUpstreamGenerationCorpus()
	{
		var cases = LoadUpstreamCorpus();
		var failures = new List<string>();
		foreach (var testCase in cases)
		{
			var hasExpectedRule = Detector
				.Detect(testCase.Path, testCase.Content, TestContext.Current.CancellationToken)
				.Any(finding => finding.RuleId.Equals(testCase.RuleId, StringComparison.Ordinal));
			// Gitleaks has one path-only PKCS#12 rule. Hide Secrets cannot replace a
			// filename or opaque binary payload in place, so this boundary is explicit.
			if (testCase.RuleId == GitleaksSecretDetector.PathOnlyRuleId)
			{
				Assert.False(hasExpectedRule);
				continue;
			}
			if (hasExpectedRule != testCase.ShouldMatch)
			{
				failures.Add(
					$"{testCase.RuleId}: expected shouldMatch={testCase.ShouldMatch}, actual={hasExpectedRule}");
			}
		}

		Assert.Equal(302, cases.Count);
		Assert.Equal(220, cases.Select(static testCase => testCase.RuleId).Distinct(StringComparer.Ordinal).Count());
		var coveredContentRules = cases
			.Where(static testCase => testCase.ShouldMatch &&
			                          testCase.RuleId != GitleaksSecretDetector.PathOnlyRuleId)
			.Select(static testCase => testCase.RuleId)
			.Concat(SupplementalPositiveRuleIds)
			.Distinct(StringComparer.Ordinal)
			.ToArray();
		Assert.Equal(GitleaksSecretDetector.ExpectedContentRuleCount, coveredContentRules.Length);
		Assert.True(
			failures.Count == 0,
			$"The managed port diverged from the pinned Gitleaks {GitleaksSecretDetector.RulesVersion} corpus:" +
			Environment.NewLine + string.Join(Environment.NewLine, failures));
	}

	[Fact(Timeout = 5_000)]
	public void Detect_PathologicalCandidateInput_CompletesWithoutBacktrackingHang()
	{
		var content = "api_key = \"" + new string('a', 2 * 1024 * 1024) + "!\"";

		var findings = Detector.Detect(
			"src/pathological.txt",
			content,
			TestContext.Current.CancellationToken);

		Assert.DoesNotContain(findings, static finding => finding.RuleId == "generic-api-key");
	}

	[Fact]
	public void Detect_PreCanceledInput_StopsBeforeScanning()
	{
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();

		Assert.Throws<OperationCanceledException>(() => Detector.Detect(
			"src/large.txt",
			new string('x', 1024 * 1024),
			cancellation.Token));
	}

	private static IEnumerable<string> GenerateRegexGuardVariants(
		string content,
		GitleaksRuleMatchProbe match)
	{
		var variants = new HashSet<string>(StringComparer.Ordinal);
		foreach (var caseVariant in GenerateCaseVariants(content))
			variants.Add(caseVariant);

		var secretStart = match.SecretStart;
		var secretEnd = match.SecretStart + match.SecretLength;
		var replacementCharacters = new[]
		{
			'A', 'a', 'F', 'f', 'Z', 'z', '0', '9', '_', '-', '=', '%', '+', '/', '.',
			'\u00E9', '\u0301', '\u203F'
		};
		for (var offset = secretStart; offset < secretEnd; offset++)
		{
			variants.Add(content.Remove(offset, 1));
			foreach (var replacement in replacementCharacters)
				variants.Add(content[..offset] + replacement + content[(offset + 1)..]);
		}

		var insertionOffsets = new[] { secretStart, secretStart + match.SecretLength / 2, secretEnd };
		foreach (var offset in insertionOffsets.Distinct())
		foreach (var length in new[] { 1, 2, 8, 16, 32, 64, 101 })
		foreach (var character in replacementCharacters)
			variants.Add(content.Insert(offset, new string(character, length)));

		var prefixThroughSecret = content[..secretEnd];
		foreach (var terminator in new[] { string.Empty, "`", "'", "\"", " ", "\t", "\r", "\n", ";", "\\n", "\\r" })
			variants.Add(prefixThroughSecret + terminator);

		return variants;
	}

	private static IEnumerable<string> GenerateCaseVariants(string value)
	{
		yield return value;
		for (var index = 0; index < value.Length; index++)
		{
			var current = value[index];
			if (!char.IsAsciiLetter(current))
				continue;
			var toggled = char.IsAsciiLetterLower(current)
				? char.ToUpperInvariant(current)
				: char.ToLowerInvariant(current);
			yield return value[..index] + toggled + value[(index + 1)..];
		}
	}

	private static IReadOnlyList<UpstreamCorpusCase> LoadUpstreamCorpus()
	{
		using var reader = new StringReader(ReadUpstreamCorpusText());
		var cases = new List<UpstreamCorpusCase>();
		while (reader.ReadLine() is { } line)
		{
			cases.Add(JsonSerializer.Deserialize<UpstreamCorpusCase>(line, CorpusJsonOptions) ??
			          throw new InvalidDataException("The embedded Gitleaks corpus contains an invalid row."));
		}
		return cases;
	}

	private static string ReadUpstreamCorpusText()
	{
		var assembly = typeof(GitleaksSecretDetectorTests).Assembly;
		var resourceName = Assert.Single(
			assembly.GetManifestResourceNames(),
			name => name.EndsWith(CorpusResourceSuffix, StringComparison.Ordinal));
		using var stream = assembly.GetManifestResourceStream(resourceName) ??
		                   throw new InvalidOperationException("The embedded Gitleaks corpus is unavailable.");
		using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		return reader.ReadToEnd();
	}

	private sealed record UpstreamCorpusCase(
		string RuleId,
		string Path,
		string? ContentBase64,
		string[]? ContentBase64Parts,
		bool ShouldMatch)
	{
		public string Content
		{
			get
			{
				var encoded = ContentBase64 ??
				              (ContentBase64Parts is { Length: > 0 }
					              ? string.Concat(ContentBase64Parts)
					              : throw new InvalidOperationException("The corpus case has no encoded content."));
				return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
			}
		}
	}

	private sealed record ShapeGateAuditRow(
		string RuleId,
		string RegexRequirement,
		string GateGuarantee,
		string Verdict,
		bool Corrected);
}

public sealed class SecretRedactionSessionTests
{
	[Fact]
	public void OutputScope_ReusesIdentityIndexAcrossFilesAndIsDeterministic()
	{
		var detector = new StubDetector("telegram-bot-api-token", StubDetector.Secret);
		using var workspace = new TemporaryDirectory();
		var root = workspace.Path;
		var paths = new[]
		{
			workspace.CreateFile("a.cs", $"token={StubDetector.Secret}"),
			workspace.CreateFile("b.cs", $"token={StubDetector.Secret}")
		};
		var firstSession = new SecretRedactionSession(detector);
		var secondSession = new SecretRedactionSession(detector);

		var first = RedactBoth(firstSession, paths, TestContext.Current.CancellationToken);
		var second = RedactBoth(secondSession, paths, TestContext.Current.CancellationToken);

		Assert.Equal(first, second);
		Assert.All(first, text =>
			Assert.Contains("DEVPROJEX_REDACTED[telegram-bot-api-token#1]", text, StringComparison.Ordinal));
	}

	[Fact]
	public void ToggleKeepAsIs_ChangesOnlyRequestedOccurrence()
	{
		const string secret = "ghp_" + "a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL";
		var detector = new StubDetector("github-pat", secret);
		using var workspace = new TemporaryDirectory();
		var root = workspace.Path;
		var paths = new[]
		{
			workspace.CreateFile("a.cs", $"a={secret}"),
			workspace.CreateFile("b.cs", $"b={secret}")
		};
		var session = new SecretRedactionSession(detector);
		var firstScope = session.BeginOutput(root, paths);
		var first = firstScope.Redact(paths[0], $"a={secret}", TestContext.Current.CancellationToken);
		_ = firstScope.Redact(paths[1], $"b={secret}", TestContext.Current.CancellationToken);
		firstScope.Complete();

		var occurrence = Assert.Single(first.Spans);
		Assert.True(session.ToggleKeepAsIs(occurrence.OccurrenceId));
		var secondScope = session.BeginOutput(root, paths);
		var kept = secondScope.Redact(paths[0], $"a={secret}", TestContext.Current.CancellationToken);
		var stillRedacted = secondScope.Redact(paths[1], $"b={secret}", TestContext.Current.CancellationToken);
		secondScope.Complete();

		Assert.Contains(secret, kept.Text, StringComparison.Ordinal);
		Assert.Equal(SecretPreviewSpanState.KeptAsIs, Assert.Single(kept.Spans).State);
		Assert.DoesNotContain(secret, stillRedacted.Text, StringComparison.Ordinal);
		Assert.Equal(1, session.GetRedactionCount(root, paths));
	}

	[Fact]
	public void SetKeepAsIs_BatchesChangesIntoOneRevisionAndOneNotification()
	{
		var detector = new StubDetector("telegram-bot-api-token", StubDetector.Secret);
		using var workspace = new TemporaryDirectory();
		var paths = new[]
		{
			workspace.CreateFile("a.cs", $"a={StubDetector.Secret}"),
			workspace.CreateFile("b.cs", $"b={StubDetector.Secret}")
		};
		using var session = new SecretRedactionSession(detector);
		var firstScope = session.BeginOutput(workspace.Path, paths);
		var first = firstScope.Redact(paths[0], $"a={StubDetector.Secret}", TestContext.Current.CancellationToken);
		var second = firstScope.Redact(paths[1], $"b={StubDetector.Secret}", TestContext.Current.CancellationToken);
		firstScope.Complete();
		var occurrenceIds = new[]
		{
			Assert.Single(first.Spans).OccurrenceId,
			Assert.Single(second.Spans).OccurrenceId
		};
		var initialRevision = session.OutputRevision;
		var notificationCount = 0;
		session.OverridesChanged += (_, _) => notificationCount++;

		var changedCount = session.SetKeepAsIs(occurrenceIds, keep: true);

		Assert.Equal(2, changedCount);
		Assert.Equal(initialRevision + 1, session.OutputRevision);
		Assert.Equal(1, notificationCount);
		Assert.Null(session.GetSnapshot(workspace.Path, paths));
		var keptScope = session.BeginOutput(workspace.Path, paths);
		Assert.Equal(
			SecretPreviewSpanState.KeptAsIs,
			Assert.Single(keptScope.Redact(paths[0], $"a={StubDetector.Secret}", TestContext.Current.CancellationToken).Spans).State);
		Assert.Equal(
			SecretPreviewSpanState.KeptAsIs,
			Assert.Single(keptScope.Redact(paths[1], $"b={StubDetector.Secret}", TestContext.Current.CancellationToken).Spans).State);
		keptScope.Complete();
	}

	[Fact]
	public void SetKeepAsIs_NoOpAndMixedSetsCountOnlyActualChanges()
	{
		using var session = new SecretRedactionSession(new StubDetector("test-rule", StubDetector.Secret));
		var initialRevision = session.OutputRevision;
		var notificationCount = 0;
		session.OverridesChanged += (_, _) => notificationCount++;

		Assert.Equal(0, session.SetKeepAsIs([], keep: true));
		Assert.Equal(1, session.SetKeepAsIs(["first"], keep: true));
		var afterFirstRevision = session.OutputRevision;
		Assert.Equal(1, session.SetKeepAsIs(["first", "second", "second"], keep: true));
		Assert.Equal(0, session.SetKeepAsIs(["first", "second"], keep: true));
		Assert.Equal(1, session.SetKeepAsIs(["missing", "first"], keep: false));
		Assert.Equal(0, session.SetKeepAsIs(["first"], keep: false));

		Assert.Equal(initialRevision + 1, afterFirstRevision);
		Assert.Equal(initialRevision + 3, session.OutputRevision);
		Assert.Equal(3, notificationCount);
	}

	[Fact]
	public void SetKeepAsIs_InvalidBatchDoesNotPartiallyApply()
	{
		using var session = new SecretRedactionSession(new StubDetector("test-rule", StubDetector.Secret));
		var initialRevision = session.OutputRevision;
		var notificationCount = 0;
		session.OverridesChanged += (_, _) => notificationCount++;

		Assert.Throws<ArgumentException>(() =>
			session.SetKeepAsIs(["valid-occurrence", " "], keep: true));

		Assert.Equal(initialRevision, session.OutputRevision);
		Assert.Equal(0, notificationCount);
		Assert.Equal(0, session.SetKeepAsIs(["valid-occurrence"], keep: false));
	}

	[Fact]
	public void KeepAsIsOverride_IsDeliberatelyScopedToOneApplicationSession()
	{
		var detector = new StubDetector("telegram-bot-api-token", StubDetector.Secret);
		using var workspace = new TemporaryDirectory();
		var root = workspace.Path;
		var path = workspace.CreateFile("config.cs", StubDetector.Secret);
		var firstSession = new SecretRedactionSession(detector);
		var firstScope = firstSession.BeginOutput(root, [path]);
		var firstResult = firstScope.Redact(path, StubDetector.Secret, TestContext.Current.CancellationToken);
		firstScope.Complete();
		Assert.True(firstSession.ToggleKeepAsIs(Assert.Single(firstResult.Spans).OccurrenceId));

		var restartedSession = new SecretRedactionSession(detector);
		var restartedScope = restartedSession.BeginOutput(root, [path]);
		var restartedResult = restartedScope.Redact(
			path,
			StubDetector.Secret,
			TestContext.Current.CancellationToken);
		restartedScope.Complete();

		Assert.DoesNotContain(StubDetector.Secret, restartedResult.Text, StringComparison.Ordinal);
		Assert.Equal(SecretPreviewSpanState.Redacted, Assert.Single(restartedResult.Spans).State);
	}

	[Fact]
	public void KeepAsIsOverride_DoesNotCrossProjectRoots()
	{
		var detector = new StubDetector("telegram-bot-api-token", StubDetector.Secret);
		using var workspace = new TemporaryDirectory();
		var baseRoot = workspace.Path;
		var firstRoot = Path.Combine(baseRoot, "first");
		var secondRoot = Path.Combine(baseRoot, "second");
		var firstPath = workspace.CreateFile("first/config.cs", StubDetector.Secret);
		var secondPath = workspace.CreateFile("second/config.cs", StubDetector.Secret);
		var session = new SecretRedactionSession(detector);
		var firstScope = session.BeginOutput(firstRoot, [firstPath]);
		var firstResult = firstScope.Redact(
			firstPath,
			StubDetector.Secret,
			TestContext.Current.CancellationToken);
		firstScope.Complete();
		Assert.True(session.ToggleKeepAsIs(Assert.Single(firstResult.Spans).OccurrenceId));

		var secondScope = session.BeginOutput(secondRoot, [secondPath]);
		var secondResult = secondScope.Redact(
			secondPath,
			StubDetector.Secret,
			TestContext.Current.CancellationToken);
		secondScope.Complete();

		Assert.DoesNotContain(StubDetector.Secret, secondResult.Text, StringComparison.Ordinal);
		Assert.Equal(SecretPreviewSpanState.Redacted, Assert.Single(secondResult.Spans).State);
	}

	[Fact]
	public void OutputScope_FreezesOverridesAndDoesNotPublishAStaleCount()
	{
		var detector = new StubDetector("telegram-bot-api-token", StubDetector.Secret);
		using var workspace = new TemporaryDirectory();
		var root = workspace.Path;
		var path = workspace.CreateFile("config.cs", StubDetector.Secret);
		var session = new SecretRedactionSession(detector);
		var initialScope = session.BeginOutput(root, [path]);
		var initial = initialScope.Redact(path, StubDetector.Secret, TestContext.Current.CancellationToken);
		initialScope.Complete();

		var staleScope = session.BeginOutput(root, [path]);
		Assert.True(session.ToggleKeepAsIs(Assert.Single(initial.Spans).OccurrenceId));
		var stale = staleScope.Redact(path, StubDetector.Secret, TestContext.Current.CancellationToken);
		staleScope.Complete();

		Assert.Equal(SecretPreviewSpanState.Redacted, Assert.Single(stale.Spans).State);
		Assert.Null(session.GetRedactionCount(root, [path]));

		var currentScope = session.BeginOutput(root, [path]);
		var current = currentScope.Redact(path, StubDetector.Secret, TestContext.Current.CancellationToken);
		currentScope.Complete();
		Assert.Equal(SecretPreviewSpanState.KeptAsIs, Assert.Single(current.Spans).State);
		Assert.Equal(0, session.GetRedactionCount(root, [path]));
	}

	[Fact]
	public void KeepAsIsOverride_FailsClosedWhenTheFindingMovesWithinTheFile()
	{
		var detector = new StubDetector("telegram-bot-api-token", StubDetector.Secret);
		using var workspace = new TemporaryDirectory();
		var root = workspace.Path;
		var path = workspace.CreateFile("config.cs", $"token={StubDetector.Secret}");
		var session = new SecretRedactionSession(detector);
		var initialScope = session.BeginOutput(root, [path]);
		var initial = initialScope.Redact(path, $"token={StubDetector.Secret}", TestContext.Current.CancellationToken);
		initialScope.Complete();
		Assert.True(session.ToggleKeepAsIs(Assert.Single(initial.Spans).OccurrenceId));

		var editedContent = $"inserted=true\ntoken={StubDetector.Secret}";
		File.WriteAllText(path, editedContent);
		File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
		var editedScope = session.BeginOutput(root, [path]);
		var edited = editedScope.Redact(
			path,
			editedContent,
			TestContext.Current.CancellationToken);
		editedScope.Complete();

		Assert.Equal(SecretPreviewSpanState.Redacted, Assert.Single(edited.Spans).State);
		Assert.DoesNotContain(StubDetector.Secret, edited.Text, StringComparison.Ordinal);
	}

	[Fact(Timeout = 10_000)]
	public void OutputScope_HighDensityFindingsResolveWithoutQuadraticOverlapWork()
	{
		const int findingCount = SecretInspectionLimits.MaximumFindingsPerFile;
		const string secret = "value-123456";
		var content = string.Join('|', Enumerable.Repeat(secret, findingCount));
		using var workspace = new TemporaryDirectory();
		var root = workspace.Path;
		var path = workspace.CreateFile("dense.env", content);
		var session = new SecretRedactionSession(new DenseDetector(secret));
		var scope = session.BeginOutput(root, [path]);

		var result = scope.Redact(path, content, TestContext.Current.CancellationToken);
		var snapshot = scope.Complete();

		Assert.Equal(findingCount, result.RedactedCount);
		Assert.Equal(findingCount, snapshot.RedactedCount);
		Assert.Equal(findingCount, result.Spans.Count);
		Assert.DoesNotContain(secret, result.Text, StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_REDACTED[dense-test#1]", result.Text, StringComparison.Ordinal);
	}

	private static string[] RedactBoth(
		SecretRedactionSession session,
		IReadOnlyList<string> paths,
		CancellationToken cancellationToken)
	{
		var root = Path.GetDirectoryName(paths[0])!;
		var scope = session.BeginOutput(root, paths);
		var output = paths.Select(path => scope.Redact(
			path,
			$"token={StubDetector.Secret}",
			cancellationToken).Text).ToArray();
		scope.Complete();
		return output;
	}

	private sealed class StubDetector(string ruleId, string secret) : ISecretDetector
	{
		public const string Secret = "123456789:" + "Aa7d9_mq2xk4vn8sr6ty3uw5zb1ce0fg2h";

		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default)
		{
			var start = content.IndexOf(secret, StringComparison.Ordinal);
			return start < 0 ? [] : [new DetectedSecret(ruleId, start, secret.Length, secret, 0)];
		}
	}

	private sealed class DenseDetector(string secret) : ISecretDetector
	{
		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default)
		{
			var findings = new List<DetectedSecret>();
			var start = 0;
			while ((start = content.IndexOf(secret, start, StringComparison.Ordinal)) >= 0)
			{
				findings.Add(new DetectedSecret("dense-test", start, secret.Length, secret, 0));
				start += secret.Length;
			}

			return findings;
		}
	}
}
