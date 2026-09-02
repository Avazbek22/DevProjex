using DevProjex.Application.Compression;
using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class PrivateDataRedactionCompositionTests
{
	[Fact]
	public void RulesIdentities_AreStableAndPairwiseDistinctForAllFeatureSets()
	{
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("data.txt", "plain text");
		var secretDetector = new NeedleDetector(
			"catalog:smart-secrets-v4",
			"secret-value",
			"secret-rule",
			RedactionFindingCategory.Secrets);
		using var session = SecretRedactionSession.CreateWithPrivateData(secretDetector, new PrivateDataDetector());

		var secretsIdentity = session
			.CreateDetectorScope(workspace.Path, SecretRedactionFeatures.Secrets)
			.GetRulesIdentity(path, "data.txt");
		var privacyIdentity = session
			.CreateDetectorScope(workspace.Path, SecretRedactionFeatures.PrivateData)
			.GetRulesIdentity(path, "data.txt");
		var combinedIdentity = session
			.CreateDetectorScope(
				workspace.Path,
				SecretRedactionFeatures.Secrets | SecretRedactionFeatures.PrivateData)
			.GetRulesIdentity(path, "data.txt");

		Assert.Equal(secretDetector.RulesIdentity, secretsIdentity);
		Assert.Equal("private-data-v1", privacyIdentity);
		Assert.Equal($"{secretsIdentity}+{privacyIdentity}", combinedIdentity);
		Assert.Equal(3, new HashSet<string>([secretsIdentity, privacyIdentity, combinedIdentity]).Count);
	}

	[Fact]
	public void Cache_NeverReusesFindingsAcrossFeatureConfigurations()
	{
		const string content = "secret-value owner@corp.io";
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("data.txt", content);
		var secrets = new NeedleDetector(
			"catalog:smart-secrets-v4",
			"secret-value",
			"secret-rule",
			RedactionFindingCategory.Secrets);
		var privacy = new NeedleDetector(
			"private-data-v1",
			"owner@corp.io",
			"email",
			RedactionFindingCategory.PrivateData);
		using var session = SecretRedactionSession.CreateWithPrivateData(secrets, privacy);

		AssertSnapshot(session, workspace.Path, path, content, SecretRedactionFeatures.Secrets, 1, 0);
		AssertSnapshot(session, workspace.Path, path, content, SecretRedactionFeatures.PrivateData, 0, 1);
		AssertSnapshot(
			session,
			workspace.Path,
			path,
			content,
			SecretRedactionFeatures.Secrets | SecretRedactionFeatures.PrivateData,
			1,
			1);
		Assert.Equal(2, secrets.CallCount);
		Assert.Equal(2, privacy.CallCount);

		AssertSnapshot(session, workspace.Path, path, content, SecretRedactionFeatures.Secrets, 1, 0);
		Assert.Equal(2, secrets.CallCount);
		Assert.Equal(2, privacy.CallCount);
		Assert.Equal(3, session.GetCacheDiagnostics().EntryCount);
	}

	[Fact]
	public void ExactOverlap_KeepSecretFallsBackToPrivateFinding()
	{
		const string content = "value=shared-sensitive-value";
		const string secretValue = "shared-sensitive-value";
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("data.txt", content);
		var secrets = new NeedleDetector(
			"catalog:smart-secrets-v4",
			secretValue,
			"secret-rule",
			RedactionFindingCategory.Secrets);
		var privateData = new NeedleDetector(
			"private-data-v1",
			secretValue,
			"private-rule",
			RedactionFindingCategory.PrivateData);
		using var session = SecretRedactionSession.CreateWithPrivateData(
			secrets,
			privateData);
		var features = SecretRedactionFeatures.Secrets | SecretRedactionFeatures.PrivateData;

		var firstScope = session.BeginOutput(workspace.Path, [path], features: features);
		var first = firstScope.Redact(path, content, TestContext.Current.CancellationToken);
		var firstSnapshot = firstScope.Complete();
		var secretSpan = Assert.Single(first.Spans);
		Assert.Equal("secret-rule", secretSpan.RuleId);
		Assert.DoesNotContain("DEVPROJEX_REDACTED[private-rule", first.Text, StringComparison.Ordinal);
		Assert.Equal(1, firstSnapshot.SecretDetectedCount);
		Assert.Equal(0, firstSnapshot.PrivateDataDetectedCount);
		AssertCountOnlyMatches(session, workspace.Path, path, content, features, firstSnapshot);

		Assert.True(session.ToggleKeepAsIs(secretSpan.OccurrenceId));
		var secondScope = session.BeginOutput(workspace.Path, [path], features: features);
		var second = secondScope.Redact(path, content, TestContext.Current.CancellationToken);
		var secondSnapshot = secondScope.Complete();

		var privateSpan = Assert.Single(second.Spans);
		Assert.Equal("private-rule", privateSpan.RuleId);
		Assert.Equal(SecretPreviewSpanState.Redacted, privateSpan.State);
		Assert.NotEqual(secretSpan.OccurrenceId, privateSpan.OccurrenceId);
		Assert.Contains("DEVPROJEX_REDACTED[private-rule#1]", second.Text, StringComparison.Ordinal);
		Assert.Equal(0, secondSnapshot.SecretDetectedCount);
		Assert.Equal(1, secondSnapshot.PrivateDataDetectedCount);
		Assert.Equal(1, secondSnapshot.RedactedCount);
		AssertCountOnlyMatches(session, workspace.Path, path, content, features, secondSnapshot);

		Assert.True(session.ToggleKeepAsIs(privateSpan.OccurrenceId));
		var fullyKept = Redact(session, workspace.Path, path, content, features);
		Assert.Equal(content, fullyKept.Result.Text);
		var keptSpan = Assert.Single(fullyKept.Result.Spans);
		Assert.Equal(SecretPreviewSpanState.KeptAsIs, keptSpan.State);
		Assert.Equal((1, 0), (
			fullyKept.Snapshot.SecretDetectedCount,
			fullyKept.Snapshot.PrivateDataDetectedCount));
		Assert.Equal(0, fullyKept.Snapshot.RedactedCount);
		AssertCountOnlyMatches(session, workspace.Path, path, content, features, fullyKept.Snapshot);
		Assert.Equal(2, Assert.IsAssignableFrom<IReadOnlyCollection<string>>(
			keptSpan.CascadedOccurrenceIds).Count);

		Assert.Equal(2, session.SetKeepAsIs(keptSpan.CascadedOccurrenceIds!, keep: false));
		var restored = Redact(session, workspace.Path, path, content, features);
		Assert.Equal(first.Text, restored.Result.Text);
		Assert.Equal(secretSpan.OccurrenceId, Assert.Single(restored.Result.Spans).OccurrenceId);
		Assert.Equal(1, secrets.CallCount);
		Assert.Equal(1, privateData.CallCount);
	}

	[Theory]
	[InlineData("AASECRETBB", "SECRET", "AASECRETBB", 1, 1)]
	[InlineData("AASECRETBB", "AASECRETBB", "SECRET", 1, 0)]
	[InlineData("AASECRETBB", "AASECRET", "SECRETBB", 1, 1)]
	public void CrossCategoryOverlap_SegmentsCoverageWithoutDuplicatingCandidateCounts(
		string content,
		string secretValue,
		string privateValue,
		int expectedSecretCount,
		int expectedPrivateCount)
	{
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("data.txt", content);
		using var session = SecretRedactionSession.CreateWithPrivateData(
			new NeedleDetector(
				"catalog:smart-secrets-v4",
				secretValue,
				"secret-rule",
				RedactionFindingCategory.Secrets),
			new NeedleDetector(
				"private-data-v1",
				privateValue,
				"private-rule",
				RedactionFindingCategory.PrivateData));

		var scan = Redact(
			session,
			workspace.Path,
			path,
			content,
			SecretRedactionFeatures.Secrets | SecretRedactionFeatures.PrivateData);

		Assert.Equal(expectedSecretCount, scan.Snapshot.SecretDetectedCount);
		Assert.Equal(expectedPrivateCount, scan.Snapshot.PrivateDataDetectedCount);
		Assert.Equal(3, scan.Result.Spans.Count);
		Assert.Single(scan.Result.Spans
			.Where(static span => span.RuleId == "secret-rule")
			.Select(static span => span.OccurrenceId)
			.Distinct(StringComparer.Ordinal));
		Assert.Equal(
			expectedPrivateCount,
			scan.Result.Spans
				.Where(static span => span.RuleId == "private-rule")
				.Select(static span => span.OccurrenceId)
				.Distinct(StringComparer.Ordinal)
				.Count());
		Assert.DoesNotContain(secretValue, scan.Result.Text, StringComparison.Ordinal);
	}

	[Fact]
	public void ManualSecretMark_InsideWiderSecretFinding_PreservesFindingFlanksAndSharedIdentity()
	{
		const string value = "left-private-flank-MANUAL-SECRET-right-private-flank";
		const string manualValue = "MANUAL-SECRET";
		const string content = "value=" + value;
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("data.txt", content);
		using var session = SecretRedactionSession.CreateWithPrivateData(
			new NeedleDetector(
				"catalog:smart-secrets-v4",
				value,
				"secret-rule",
				RedactionFindingCategory.Secrets),
			new NeedleDetector(
				"private-data-v1",
				"not-present",
				"private-rule",
				RedactionFindingCategory.PrivateData));
		Assert.True(MarkedSecretValueNormalizer.TryCreate(manualValue, out var mark, out _));
		Assert.True(session.AddSessionMarkedSecret(
			"data.txt",
			content.IndexOf(manualValue, StringComparison.Ordinal),
			mark,
			ManualRedactionClass.Secret));

		var scan = Redact(
			session,
			workspace.Path,
			path,
			content,
			SecretRedactionFeatures.Secrets | SecretRedactionFeatures.PrivateData);

		Assert.Equal(3, scan.Result.Spans.Count);
		var manualSpan = Assert.Single(scan.Result.Spans, static span => span.RuleId == "manual-secret");
		var detectorSpans = scan.Result.Spans.Where(static span => span.RuleId == "secret-rule").ToArray();
		Assert.Equal(2, detectorSpans.Length);
		Assert.Single(detectorSpans.Select(static span => span.OccurrenceId).Distinct(StringComparer.Ordinal));
		Assert.All(detectorSpans, static span => Assert.Equal(SecretFindingSource.Detector, span.Source));
		Assert.True(manualSpan.Source.HasFlag(SecretFindingSource.SessionMark));
		Assert.False(manualSpan.Source.HasFlag(SecretFindingSource.Detector));
		Assert.Equal(
			"value=DEVPROJEX_REDACTED[secret-rule#1]" +
			"DEVPROJEX_REDACTED[manual-secret#1]" +
			"DEVPROJEX_REDACTED[secret-rule#1]",
			scan.Result.Text);
		Assert.DoesNotContain("left-private-flank", scan.Result.Text, StringComparison.Ordinal);
		Assert.DoesNotContain(manualValue, scan.Result.Text, StringComparison.Ordinal);
		Assert.DoesNotContain("right-private-flank", scan.Result.Text, StringComparison.Ordinal);
		Assert.Equal((2, 0), (scan.Snapshot.SecretRedactedCount, scan.Snapshot.PrivateDataRedactedCount));

		Assert.Equal(1, session.SetKeepAsIs([detectorSpans[0].OccurrenceId], keep: true));
		var keptFinding = Redact(
			session,
			workspace.Path,
			path,
			content,
			SecretRedactionFeatures.Secrets | SecretRedactionFeatures.PrivateData);
		Assert.Equal(
			"value=left-private-flank-" +
			"DEVPROJEX_REDACTED[manual-secret#1]" +
			"-right-private-flank",
			keptFinding.Result.Text);
		Assert.Equal(1, session.SetKeepAsIs([detectorSpans[0].OccurrenceId], keep: false));
		Assert.Equal(scan.Result.Text, Redact(
			session,
			workspace.Path,
			path,
			content,
			SecretRedactionFeatures.Secrets | SecretRedactionFeatures.PrivateData).Result.Text);
	}

	[Fact]
	public void PersistentSecretMark_InsideWiderSecretFinding_PreservesFindingFlanksAndSharedIdentity()
	{
		const string value = "left-detector-flank-PERSISTENT-MARK-right-detector-flank";
		const string markedValue = "PERSISTENT-MARK";
		const string content = "value=" + value;
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("data.txt", content);
		using var session = SecretRedactionSession.CreateWithPrivateData(
			new NeedleDetector(
				"catalog:smart-secrets-v4",
				value,
				"secret-rule",
				RedactionFindingCategory.Secrets),
			new NeedleDetector(
				"private-data-v1",
				"not-present",
				"private-rule",
				RedactionFindingCategory.PrivateData));
		Assert.True(MarkedSecretValueNormalizer.TryCreate(markedValue, out var mark, out _));
		session.ReplacePersistentMarks(
			workspace.Path,
			new PersistentSecretMarksSnapshot(
				1,
				[
					new MarkedSecretProfileEntry(
						mark.Hash,
						null,
						mark.Length)
				]));

		var scan = Redact(
			session,
			workspace.Path,
			path,
			content,
			SecretRedactionFeatures.Secrets);

		var persistentSpan = Assert.Single(
			scan.Result.Spans,
			static span => span.RuleId == "manual-secret");
		var detectorSpans = scan.Result.Spans
			.Where(static span => span.RuleId == "secret-rule")
			.ToArray();
		Assert.Equal(2, detectorSpans.Length);
		Assert.Single(detectorSpans.Select(static span => span.OccurrenceId).Distinct(StringComparer.Ordinal));
		Assert.True(persistentSpan.Source.HasFlag(SecretFindingSource.PersistentMark));
		Assert.NotNull(persistentSpan.PersistentMarkId);
		Assert.DoesNotContain("left-detector-flank", scan.Result.Text, StringComparison.Ordinal);
		Assert.DoesNotContain(markedValue, scan.Result.Text, StringComparison.Ordinal);
		Assert.DoesNotContain("right-detector-flank", scan.Result.Text, StringComparison.Ordinal);
		Assert.Equal(2, scan.Snapshot.SecretRedactedCount);
		AssertCountOnlyMatches(
			session,
			workspace.Path,
			path,
			content,
			SecretRedactionFeatures.Secrets,
			scan.Snapshot);
	}

	[Fact]
	public void ThreeNestedSameCategoryMarks_AllSurviveAndCoverTheOuterRange()
	{
		const string content = "0123456789abcdefghijklmnopqrst";
		DetectedSecret[] findings =
		[
			new("outer", 0, 30, content, -100, SecretFindingSource.SessionMark, SessionMarkId: "outer"),
			new("middle", 5, 20, content.Substring(5, 20), -200, SecretFindingSource.SessionMark, SessionMarkId: "middle"),
			new("inner", 10, 10, content.Substring(10, 10), -300, SecretFindingSource.SessionMark, SessionMarkId: "inner")
		];

		var resolved = SecretRedactionScope.ResolveSegmentedFindings(findings);

		Assert.Equal(3, resolved.Candidates.Count);
		Assert.Equal(5, resolved.Segments.Count);
		Assert.All(GetCoverage(content.Length, resolved.Segments), Assert.True);
		Assert.Equal(
			[1, 2, 3, 2, 1],
			resolved.Segments.Select(static segment => segment.CandidateIndexes.Count).ToArray());
	}

	[Fact]
	public void SameCategoryOverlap_WithCompressionMap_PreservesCoordinatesAndCoverage()
	{
		const string prefix = "// removed by compression\n";
		const string value = "left-detector-flank-MANUAL-MARK-right-detector-flank";
		const string markedValue = "MANUAL-MARK";
		const string relativePath = "data.cs";
		var source = prefix + "value=" + value;
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile(relativePath, source);
		using var session = SecretRedactionSession.CreateWithPrivateData(
			new NeedleDetector(
				"catalog:smart-secrets-v4",
				value,
				"secret-rule",
				RedactionFindingCategory.Secrets),
			new NeedleDetector(
				"private-data-v1",
				"not-present",
				"private-rule",
				RedactionFindingCategory.PrivateData));
		Assert.True(MarkedSecretValueNormalizer.TryCreate(markedValue, out var mark, out _));
		Assert.True(session.AddSessionMarkedSecret(
			relativePath,
			source.IndexOf(markedValue, StringComparison.Ordinal),
			mark,
			ManualRedactionClass.Secret));
		var compression = CodeCompressionPlan.Create(
			relativePath,
			"csharp",
			[new CodeCompressionEdit(0, prefix.Length, string.Empty)],
			source.Length,
			"composition-transform-v1").Apply(source);
		var scope = session.BeginOutput(
			workspace.Path,
			[path],
			"composition-transform-v1",
			SecretRedactionFeatures.Secrets);

		var result = scope.Redact(
			path,
			compression.Text,
			compression.Map,
			TestContext.Current.CancellationToken);
		var snapshot = scope.Complete();

		var detectorSpans = result.Spans.Where(static span => span.RuleId == "secret-rule").ToArray();
		Assert.Equal(2, detectorSpans.Length);
		Assert.Single(detectorSpans.Select(static span => span.OccurrenceId).Distinct(StringComparer.Ordinal));
		Assert.Single(result.Spans, static span => span.RuleId == "manual-secret");
		Assert.DoesNotContain("left-detector-flank", result.Text, StringComparison.Ordinal);
		Assert.DoesNotContain(markedValue, result.Text, StringComparison.Ordinal);
		Assert.DoesNotContain("right-detector-flank", result.Text, StringComparison.Ordinal);
		Assert.All(result.Spans, span => Assert.InRange(span.Start + span.Length, 1, result.Text.Length));
		Assert.Equal(2, snapshot.SecretRedactedCount);
	}

	[Fact]
	public void ExactCrossCategoryGroup_InsideManualPrivateDataMark_PreservesSecretFlanks()
	{
		const string value = "left-secret-flank-MANUAL-PRIVATE-right-secret-flank";
		const string manualValue = "MANUAL-PRIVATE";
		const string content = "value=" + value;
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("data.txt", content);
		using var session = SecretRedactionSession.CreateWithPrivateData(
			new NeedleDetector(
				"catalog:smart-secrets-v4",
				value,
				"secret-rule",
				RedactionFindingCategory.Secrets),
			new NeedleDetector(
				"private-data-v1",
				value,
				"private-rule",
				RedactionFindingCategory.PrivateData));
		Assert.True(MarkedSecretValueNormalizer.TryCreate(manualValue, out var mark, out _));
		Assert.True(session.AddSessionMarkedSecret(
			"data.txt",
			content.IndexOf(manualValue, StringComparison.Ordinal),
			mark,
			ManualRedactionClass.PrivateData));

		var scan = Redact(
			session,
			workspace.Path,
			path,
			content,
			SecretRedactionFeatures.Secrets | SecretRedactionFeatures.PrivateData);

		Assert.Equal(3, scan.Result.Spans.Count);
		Assert.Single(scan.Result.Spans, static span => span.RuleId == "manual-private-data");
		Assert.Equal(2, scan.Result.Spans.Count(static span => span.RuleId == "secret-rule"));
		Assert.Equal(
			"value=DEVPROJEX_REDACTED[secret-rule#1]" +
			"DEVPROJEX_REDACTED[manual-private-data#1]" +
			"DEVPROJEX_REDACTED[secret-rule#1]",
			scan.Result.Text);
		Assert.DoesNotContain("left-secret-flank", scan.Result.Text, StringComparison.Ordinal);
		Assert.DoesNotContain(manualValue, scan.Result.Text, StringComparison.Ordinal);
		Assert.DoesNotContain("right-secret-flank", scan.Result.Text, StringComparison.Ordinal);
		Assert.Equal((1, 1), (scan.Snapshot.SecretRedactedCount, scan.Snapshot.PrivateDataRedactedCount));

		var secretOccurrenceIds = scan.Result.Spans
			.Where(static span => span.RuleId == "secret-rule")
			.Select(static span => span.OccurrenceId)
			.ToArray();
		Assert.Equal(1, session.SetKeepAsIs(secretOccurrenceIds, keep: true));
		var keptFlanks = Redact(
			session,
			workspace.Path,
			path,
			content,
			SecretRedactionFeatures.Secrets | SecretRedactionFeatures.PrivateData);
		Assert.Equal(
			"value=DEVPROJEX_REDACTED[private-rule#1]" +
			"DEVPROJEX_REDACTED[manual-private-data#1]" +
			"DEVPROJEX_REDACTED[private-rule#1]",
			keptFlanks.Result.Text);
		Assert.DoesNotContain("left-secret-flank", keptFlanks.Result.Text, StringComparison.Ordinal);
		Assert.DoesNotContain("right-secret-flank", keptFlanks.Result.Text, StringComparison.Ordinal);
	}

	[Fact]
	public void ExactCrossCategoryGroup_InsideNonGenericSecret_PreservesPrivateDataFlanks()
	{
		const string value = "left-private-flank-PRIORITY-SECRET-right-private-flank";
		const string priorityValue = "PRIORITY-SECRET";
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("data.txt", value);
		using var session = SecretRedactionSession.CreateWithPrivateData(
			new FixedFindingsDetector(
				"catalog:smart-secrets-v4",
				[
					CreateFinding(value, value, "generic-api-key", RedactionFindingCategory.Secrets),
					CreateFinding(value, priorityValue, "priority-secret", RedactionFindingCategory.Secrets)
				]),
			new NeedleDetector(
				"private-data-v1",
				value,
				"private-rule",
				RedactionFindingCategory.PrivateData));

		var scan = Redact(
			session,
			workspace.Path,
			path,
			value,
			SecretRedactionFeatures.Secrets | SecretRedactionFeatures.PrivateData);

		Assert.Equal(3, scan.Result.Spans.Count);
		Assert.Single(scan.Result.Spans, static span => span.RuleId == "priority-secret");
		Assert.Equal(2, scan.Result.Spans.Count(static span => span.RuleId == "private-rule"));
		Assert.Equal(
			"DEVPROJEX_REDACTED[private-rule#1]" +
			"DEVPROJEX_REDACTED[priority-secret#1]" +
			"DEVPROJEX_REDACTED[private-rule#1]",
			scan.Result.Text);
		Assert.DoesNotContain("left-private-flank", scan.Result.Text, StringComparison.Ordinal);
		Assert.DoesNotContain(priorityValue, scan.Result.Text, StringComparison.Ordinal);
		Assert.DoesNotContain("right-private-flank", scan.Result.Text, StringComparison.Ordinal);
		Assert.DoesNotContain("DEVPROJEX_REDACTED[generic-api-key", scan.Result.Text, StringComparison.Ordinal);
		Assert.Equal((1, 1), (scan.Snapshot.SecretRedactedCount, scan.Snapshot.PrivateDataRedactedCount));
	}

	[Fact]
	public void TriplePriorityOverlap_RedactsEveryPositionCoveredByASurvivingCategory()
	{
		const string value = "left-private-flank-priority-MANUAL-MARK-secret-right-private-flank";
		const string priorityValue = "priority-MANUAL-MARK-secret";
		const string manualValue = "MANUAL-MARK";
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("data.txt", value);
		using var session = SecretRedactionSession.CreateWithPrivateData(
			new FixedFindingsDetector(
				"catalog:smart-secrets-v4",
				[
					CreateFinding(value, value, "generic-api-key", RedactionFindingCategory.Secrets),
					CreateFinding(value, priorityValue, "priority-secret", RedactionFindingCategory.Secrets)
				]),
			new NeedleDetector(
				"private-data-v1",
				value,
				"private-rule",
				RedactionFindingCategory.PrivateData));
		Assert.True(MarkedSecretValueNormalizer.TryCreate(manualValue, out var mark, out _));
		Assert.True(session.AddSessionMarkedSecret(
			"data.txt",
			value.IndexOf(manualValue, StringComparison.Ordinal),
			mark,
			ManualRedactionClass.Secret));

		var scan = Redact(
			session,
			workspace.Path,
			path,
			value,
			SecretRedactionFeatures.Secrets | SecretRedactionFeatures.PrivateData);

		Assert.Equal(5, scan.Result.Spans.Count);
		Assert.Single(scan.Result.Spans, static span => span.RuleId == "manual-secret");
		Assert.Equal(2, scan.Result.Spans.Count(static span => span.RuleId == "private-rule"));
		Assert.Equal(2, scan.Result.Spans.Count(static span => span.RuleId == "priority-secret"));
		Assert.Equal(
			"DEVPROJEX_REDACTED[private-rule#1]" +
			"DEVPROJEX_REDACTED[priority-secret#1]" +
			"DEVPROJEX_REDACTED[manual-secret#1]" +
			"DEVPROJEX_REDACTED[priority-secret#1]" +
			"DEVPROJEX_REDACTED[private-rule#1]",
			scan.Result.Text);
		Assert.DoesNotContain("left-private-flank", scan.Result.Text, StringComparison.Ordinal);
		Assert.DoesNotContain(priorityValue, scan.Result.Text, StringComparison.Ordinal);
		Assert.DoesNotContain("right-private-flank", scan.Result.Text, StringComparison.Ordinal);
		Assert.DoesNotContain("DEVPROJEX_REDACTED[generic-api-key", scan.Result.Text, StringComparison.Ordinal);
		Assert.Equal((2, 1), (scan.Snapshot.SecretRedactedCount, scan.Snapshot.PrivateDataRedactedCount));
	}

	[Fact]
	public void PartialCrossCategoryOverlap_KeepFallsBackAndFullyKeptSegmentRestoresWholeStack()
	{
		const string content = "private-prefix-SECRET-private-suffix";
		const string secretValue = "SECRET";
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("data.txt", content);
		var findings = new[]
		{
			CreateFinding(content, secretValue, "secret-rule", RedactionFindingCategory.Secrets),
			CreateFinding(content, content, "private-rule", RedactionFindingCategory.PrivateData)
		};
		using var session = CreateSessionWithFixedFindings(findings);
		var features = SecretRedactionFeatures.Secrets | SecretRedactionFeatures.PrivateData;

		var initial = Redact(session, workspace.Path, path, content, features);
		AssertCountOnlyMatches(session, workspace.Path, path, content, features, initial.Snapshot);
		var secretSpan = Assert.Single(initial.Result.Spans, static span => span.RuleId == "secret-rule");
		var privateSpans = initial.Result.Spans.Where(static span => span.RuleId == "private-rule").ToArray();
		Assert.Equal(2, privateSpans.Length);
		Assert.Single(privateSpans.Select(static span => span.OccurrenceId).Distinct(StringComparer.Ordinal));
		Assert.Equal(2, CountOccurrences(initial.Result.Text, "DEVPROJEX_REDACTED[private-rule#1]"));

		Assert.True(session.ToggleKeepAsIs(secretSpan.OccurrenceId));
		var secretKept = Redact(session, workspace.Path, path, content, features);
		AssertCountOnlyMatches(session, workspace.Path, path, content, features, secretKept.Snapshot);
		Assert.Equal(3, secretKept.Result.Spans.Count);
		Assert.All(secretKept.Result.Spans, static span => Assert.Equal("private-rule", span.RuleId));
		Assert.DoesNotContain(secretValue, secretKept.Result.Text, StringComparison.Ordinal);

		var privateOccurrenceId = secretKept.Result.Spans[0].OccurrenceId;
		Assert.True(session.ToggleKeepAsIs(privateOccurrenceId));
		var fullyKept = Redact(session, workspace.Path, path, content, features);
		AssertCountOnlyMatches(session, workspace.Path, path, content, features, fullyKept.Snapshot);
		Assert.Equal(content, fullyKept.Result.Text);
		var cascade = Assert.Single(
			fullyKept.Result.Spans,
			static span => span.CascadedOccurrenceIds is { Count: 2 });
		Assert.Equal(2, session.SetKeepAsIs(cascade.CascadedOccurrenceIds!, keep: false));
		Assert.Equal(initial.Result.Text, Redact(session, workspace.Path, path, content, features).Result.Text);
	}

	[Fact]
	public void DetectorOnlySameCategoryOverlap_NarrowSpecificSuppressesWideGenericEntirely()
	{
		const string content = "name=left-SPECIFIC-right";
		const string specific = "SPECIFIC";
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("data.txt", content);
		using var session = CreateSessionWithFixedFindings(
		[
			CreateFinding(content, content, "generic-api-key", RedactionFindingCategory.Secrets),
			CreateFinding(content, specific, "specific-secret", RedactionFindingCategory.Secrets)
		]);

		var scan = Redact(session, workspace.Path, path, content, SecretRedactionFeatures.Secrets);

		Assert.Equal("name=left-DEVPROJEX_REDACTED[specific-secret#1]-right", scan.Result.Text);
		Assert.Single(scan.Result.Spans);
		Assert.Equal(1, scan.Snapshot.SecretRedactedCount);
		Assert.DoesNotContain("generic-api-key", scan.Result.Text, StringComparison.Ordinal);
	}

	[Fact]
	public void OverlappingManualMarks_SameCategoryStackWithoutOpeningEachOther()
	{
		const string content = "value=ABCDEFGHIJKLMN";
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("data.txt", content);
		using var session = CreateSessionWithFixedFindings([]);
		Assert.True(MarkedSecretValueNormalizer.TryCreate("BCDEFGHI", out var firstMark, out _));
		Assert.True(MarkedSecretValueNormalizer.TryCreate("FGHIJKLM", out var secondMark, out _));
		Assert.True(session.AddSessionMarkedSecret("data.txt", 7, firstMark, ManualRedactionClass.Secret));
		Assert.True(session.AddSessionMarkedSecret("data.txt", 11, secondMark, ManualRedactionClass.Secret));

		var initial = Redact(session, workspace.Path, path, content, SecretRedactionFeatures.Secrets);
		Assert.Equal(3, initial.Result.Spans.Count);
		var occurrenceIds = initial.Result.Spans
			.Select(static span => span.OccurrenceId)
			.Distinct(StringComparer.Ordinal)
			.ToArray();
		Assert.Equal(2, occurrenceIds.Length);

		Assert.True(session.ToggleKeepAsIs(occurrenceIds[0]));
		var oneKept = Redact(session, workspace.Path, path, content, SecretRedactionFeatures.Secrets);
		Assert.Equal(
			"value=ABCDE" +
			"DEVPROJEX_REDACTED[manual-secret#2]" +
			"DEVPROJEX_REDACTED[manual-secret#2]N",
			oneKept.Result.Text);
		Assert.DoesNotContain("FGHIJKLM", oneKept.Result.Text, StringComparison.Ordinal);
	}

	[Fact]
	public void AddingManualMark_NeverShrinksResolvedCoverage_ForDeterministicRandomInputs()
	{
		var random = new Random(0x5EED_41);
		const string content = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789+-";
		for (var iteration = 0; iteration < 128; iteration++)
		{
			var findings = CreateRandomFindings(random, content, iteration, random.Next(1, 18));
			var before = SecretRedactionScope.ResolveSegmentedFindings(findings);
			var markStart = random.Next(0, content.Length - 1);
			var markLength = random.Next(1, content.Length - markStart + 1);
			var markCategory = random.Next(2) == 0
				? RedactionFindingCategory.Secrets
				: RedactionFindingCategory.PrivateData;
			var mark = new DetectedSecret(
				markCategory == RedactionFindingCategory.Secrets ? "manual-secret" : "manual-private-data",
				markStart,
				markLength,
				content.Substring(markStart, markLength),
				int.MinValue,
				SecretFindingSource.SessionMark,
				Category: markCategory);
			var after = SecretRedactionScope.ResolveSegmentedFindings([..findings, mark]);

			var beforeCoverage = GetCoverage(content.Length, before.Segments);
			var afterCoverage = GetCoverage(content.Length, after.Segments);
			for (var position = 0; position < content.Length; position++)
				Assert.False(beforeCoverage[position] && !afterCoverage[position]);
		}
	}

	[Fact]
	public void RandomKeepCombinations_NeverExposeSegmentsCoveredBySurvivingCandidates()
	{
		var random = new Random(0x51A7_2026);
		const string content = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789+-";
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("data.txt", content);
		var findings = CreateRandomFindings(random, content, iteration: 0, count: 24);
		var resolved = SecretRedactionScope.ResolveSegmentedFindings(findings);
		using var session = CreateSessionWithFixedFindings(findings);
		var features = SecretRedactionFeatures.Secrets | SecretRedactionFeatures.PrivateData;
		var occurrenceIdsByRule = RevealAllCandidateOccurrenceIds(
			session,
			workspace.Path,
			path,
			content,
			features,
			resolved.Candidates.Count);

		Assert.Equal(resolved.Candidates.Count, occurrenceIdsByRule.Count);
		for (var state = 0; state < 64; state++)
		{
			session.SetKeepAsIs(occurrenceIdsByRule.Values.ToArray(), keep: true);
			var redactedRules = resolved.Candidates
				.Where(_ => random.Next(2) == 0)
				.Select(static candidate => candidate.RuleId)
				.ToHashSet(StringComparer.Ordinal);
			session.SetKeepAsIs(
				redactedRules.Select(rule => occurrenceIdsByRule[rule]).ToArray(),
				keep: false);

			var scope = session.BeginOutput(workspace.Path, [path], features: features);
			var plan = scope.CreatePlan(path, content, TestContext.Current.CancellationToken);
			scope.Complete();
			Assert.Equal(resolved.Segments.Count, plan.Replacements.Count);
			for (var segmentIndex = 0; segmentIndex < resolved.Segments.Count; segmentIndex++)
			{
				var segment = resolved.Segments[segmentIndex];
				var mustBeRedacted = segment.CandidateIndexes.Any(
					index => redactedRules.Contains(resolved.Candidates[index].RuleId));
				Assert.Equal(mustBeRedacted, plan.Replacements[segmentIndex].Replacement is not null);
			}
		}
	}

	[Fact]
	public void SegmentedResolution_IsDeterministicAcrossSessionsForSeededRandomInputs()
	{
		var random = new Random(unchecked((int)0xD371_2026));
		const string content = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789+-";
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("data.txt", content);
		var findings = CreateRandomFindings(random, content, iteration: 7, count: 32);
		var features = SecretRedactionFeatures.Secrets | SecretRedactionFeatures.PrivateData;

		using var firstSession = CreateSessionWithFixedFindings(findings);
		using var secondSession = CreateSessionWithFixedFindings(findings);
		var first = Redact(firstSession, workspace.Path, path, content, features);
		var second = Redact(secondSession, workspace.Path, path, content, features);

		Assert.Equal(first.Result.Text, second.Result.Text);
		Assert.Equal(first.Result.Spans, second.Result.Spans);
		Assert.Equal(first.Snapshot.SecretDetectedCount, second.Snapshot.SecretDetectedCount);
		Assert.Equal(first.Snapshot.PrivateDataDetectedCount, second.Snapshot.PrivateDataDetectedCount);
	}

	[Fact]
	public void CombinedDetectorScope_RejectsRuleIdSharedAcrossCategories()
	{
		const string content = "shared-value";
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("data.txt", content);
		using var session = SecretRedactionSession.CreateWithPrivateData(
			new NeedleDetector(
				"catalog:smart-secrets-v4",
				content,
				"shared-rule",
				RedactionFindingCategory.Secrets),
			new NeedleDetector(
				"private-data-v1",
				content,
				"shared-rule",
				RedactionFindingCategory.PrivateData));

		var scope = session.BeginOutput(
			workspace.Path,
			[path],
			features: SecretRedactionFeatures.Secrets | SecretRedactionFeatures.PrivateData);

		Assert.Throws<SecretDetectionException>(() =>
			scope.Redact(path, content, TestContext.Current.CancellationToken));
	}

	[Fact]
	public void ExactOverlap_MergedSourceKeepsOnlyWinnerRemovalMetadata()
	{
		var winnerMarkId = new PersistentSecretMarkId("winner-hash", 12, "data.txt", 4);
		var loserMarkId = new PersistentSecretMarkId(
			"loser-hash",
			12,
			"data.txt",
			4);
		var winner = new DetectedSecret(
			"manual-secret",
			4,
			12,
			"shared-value",
			int.MinValue,
			SecretFindingSource.PersistentMark | SecretFindingSource.SessionMark,
			"winner-hash",
			"winner-session",
			winnerMarkId,
			RedactionFindingCategory.Secrets);
		var loser = new DetectedSecret(
			"detector-rule",
			4,
			12,
			"shared-value",
			int.MinValue,
			SecretFindingSource.Detector,
			"loser-hash",
			"loser-session",
			loserMarkId,
			RedactionFindingCategory.Secrets);

		var resolved = Assert.Single(SecretRedactionScope.ResolveNonOverlappingMatches([loser, winner]));

		Assert.Equal(
			SecretFindingSource.PersistentMark | SecretFindingSource.SessionMark | SecretFindingSource.Detector,
			resolved.Source);
		Assert.Equal("winner-hash", resolved.PersistentMarkHash);
		Assert.Equal("winner-session", resolved.SessionMarkId);
		Assert.Equal(winnerMarkId, resolved.PersistentMarkId);
	}

	[Fact]
	public void ExactOverlap_UnmarkRemovesOnlyTheDisplayedManualMarkClass()
	{
		const string valueText = "shared-manual-value";
		const string content = "value=" + valueText;
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("data.txt", content);
		using var session = SecretRedactionSession.CreateWithPrivateData(
			new NeedleDetector("catalog:smart-secrets-v4", "not-present", "unused", RedactionFindingCategory.Secrets),
			new NeedleDetector("private-data-v1", "also-not-present", "unused", RedactionFindingCategory.PrivateData));
		Assert.True(MarkedSecretValueNormalizer.TryCreate(valueText, out var value, out _));
		Assert.True(session.AddSessionMarkedSecret(
			"data.txt",
			"value=".Length,
			value,
			ManualRedactionClass.Secret));
		Assert.True(session.AddSessionMarkedSecret(
			"data.txt",
			"value=".Length,
			value,
			ManualRedactionClass.PrivateData));
		var features = SecretRedactionFeatures.Secrets | SecretRedactionFeatures.PrivateData;

		var secret = Redact(session, workspace.Path, path, content, features);
		var secretSpan = Assert.Single(secret.Result.Spans);
		Assert.Equal("manual-secret", secretSpan.RuleId);
		Assert.True(session.RemoveSessionMarkedSecret(Assert.IsType<string>(secretSpan.SessionMarkId)));

		var privateData = Redact(session, workspace.Path, path, content, features);
		var privateSpan = Assert.Single(privateData.Result.Spans);
		Assert.Equal("manual-private-data", privateSpan.RuleId);
		Assert.True(session.RemoveSessionMarkedSecret(Assert.IsType<string>(privateSpan.SessionMarkId)));

		var visible = Redact(session, workspace.Path, path, content, features);
		Assert.Equal(content, visible.Result.Text);
		Assert.Empty(visible.Result.Spans);
	}

	[Fact]
	public void ExactOverlap_OccurrenceIdsAndPlaceholderIndexesAreStableAcrossSessions()
	{
		const string content = "value=stable-overlap-value";
		const string value = "stable-overlap-value";
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("data.txt", content);

		var first = CaptureCascade(workspace.Path, path, content, value);
		var restarted = CaptureCascade(workspace.Path, path, content, value);

		Assert.Equal(first, restarted);

		static (string SecretText, string SecretId, string PrivateText, string PrivateId) CaptureCascade(
			string root,
			string filePath,
			string fileContent,
			string exactValue)
		{
			using var session = SecretRedactionSession.CreateWithPrivateData(
				new NeedleDetector(
					"catalog:smart-secrets-v4",
					exactValue,
					"secret-rule",
					RedactionFindingCategory.Secrets),
				new NeedleDetector(
					"private-data-v1",
					exactValue,
					"private-rule",
					RedactionFindingCategory.PrivateData));
			var features = SecretRedactionFeatures.Secrets | SecretRedactionFeatures.PrivateData;
			var secret = Redact(session, root, filePath, fileContent, features);
			var secretSpan = Assert.Single(secret.Result.Spans);
			Assert.True(session.ToggleKeepAsIs(secretSpan.OccurrenceId));
			var privateData = Redact(session, root, filePath, fileContent, features);
			var privateSpan = Assert.Single(privateData.Result.Spans);
			return (
				secret.Result.Text,
				secretSpan.OccurrenceId,
				privateData.Result.Text,
				privateSpan.OccurrenceId);
		}
	}

	[Fact]
	public void CombinedScan_ReportsSeparateCategoryCountsWithDeterministicPlaceholders()
	{
		const string content = "token=secret-value\ncontact=employee" + "@corp.io\n";
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("data.txt", content);
		var secrets = new NeedleDetector(
			"catalog:smart-secrets-v4",
			"secret-value",
			"secret-rule",
			RedactionFindingCategory.Secrets);
		using var session = SecretRedactionSession.CreateWithPrivateData(secrets, new PrivateDataDetector());
		var features = SecretRedactionFeatures.Secrets | SecretRedactionFeatures.PrivateData;

		var first = Redact(session, workspace.Path, path, content, features);
		var second = Redact(session, workspace.Path, path, content, features);

		Assert.Equal(first.Result.Text, second.Result.Text);
		Assert.Equal(first.Result.Spans, second.Result.Spans);
		Assert.Equal(1, first.Snapshot.SecretDetectedCount);
		Assert.Equal(1, first.Snapshot.SecretRedactedCount);
		Assert.Equal(1, first.Snapshot.PrivateDataDetectedCount);
		Assert.Equal(1, first.Snapshot.PrivateDataRedactedCount);
		Assert.Contains("DEVPROJEX_REDACTED[secret-rule#1]", first.Result.Text, StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_REDACTED[email#1]", first.Result.Text, StringComparison.Ordinal);
	}

	[Fact]
	public void RealDetectors_SecretSpansSuppressOverlappingEmailAndIpFindings()
	{
		const string content =
			"dsn=postgres://admin:owner@corp.internal@prod.internal/app\n" +
			"Cookie: client=93.184." + "216.34\n";
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("request.http", content);
		using var session = SecretRedactionSession.CreateWithPrivateData(
			SmartSecretsDetectorTests.Detector,
			new PrivateDataDetector());

		var scan = Redact(
			session,
			workspace.Path,
			path,
			content,
			SecretRedactionFeatures.Secrets | SecretRedactionFeatures.PrivateData);

		Assert.Equal(2, scan.Snapshot.SecretDetectedCount);
		Assert.Equal(0, scan.Snapshot.PrivateDataDetectedCount);
		Assert.Contains("DEVPROJEX_REDACTED[credential-uri-password#1]", scan.Result.Text, StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_REDACTED[http-cookie#1]", scan.Result.Text, StringComparison.Ordinal);
		Assert.DoesNotContain("DEVPROJEX_REDACTED[email", scan.Result.Text, StringComparison.Ordinal);
		Assert.DoesNotContain("DEVPROJEX_REDACTED[ipv4", scan.Result.Text, StringComparison.Ordinal);
	}

	[Fact]
	public void LocalUser_UsesOneValueIdentityAcrossFiles()
	{
		const string firstContent = "source=C:\\Users\\" + "avazb\\first";
		const string secondContent = "source=/home/" + "avazb/second";
		using var workspace = new TemporaryDirectory();
		var firstPath = workspace.CreateFile("first.txt", firstContent);
		var secondPath = workspace.CreateFile("second.txt", secondContent);
		using var session = SecretRedactionSession.CreateWithPrivateData(
			new NeedleDetector("unused", "not-present", "unused", RedactionFindingCategory.Secrets),
			new PrivateDataDetector());
		var scope = session.BeginOutput(
			workspace.Path,
			[firstPath, secondPath],
			features: SecretRedactionFeatures.PrivateData);

		var first = scope.Redact(firstPath, firstContent, TestContext.Current.CancellationToken);
		var second = scope.Redact(secondPath, secondContent, TestContext.Current.CancellationToken);
		var snapshot = scope.Complete();

		Assert.Contains("DEVPROJEX_REDACTED[local-user#1]", first.Text, StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_REDACTED[local-user#1]", second.Text, StringComparison.Ordinal);
		Assert.Equal(2, snapshot.PrivateDataDetectedCount);
		Assert.Equal(2, snapshot.PrivateDataRedactedCount);
	}

	[Fact]
	public void PrivateDataOnly_DoesNotApplyManualMarksOwnedByHideSecrets()
	{
		const string content = "value=manually-marked-value";
		const string manualValue = "manually-marked-value";
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("data.txt", content);
		using var session = SecretRedactionSession.CreateWithPrivateData(
			new NeedleDetector("catalog:smart-secrets-v4", "not-present", "unused", RedactionFindingCategory.Secrets),
			new PrivateDataDetector());
		Assert.True(MarkedSecretValueNormalizer.TryCreate(manualValue, out var markedValue, out var error), error.ToString());
		Assert.True(session.AddSessionMarkedSecret("data.txt", "value=".Length, markedValue));

		var privacyOnly = Redact(
			session,
			workspace.Path,
			path,
			content,
			SecretRedactionFeatures.PrivateData);
		var secretsOnly = Redact(
			session,
			workspace.Path,
			path,
			content,
			SecretRedactionFeatures.Secrets);

		Assert.Equal(content, privacyOnly.Result.Text);
		Assert.Empty(privacyOnly.Result.Spans);
		Assert.Equal(0, privacyOnly.Snapshot.DetectedCount);
		Assert.Contains("DEVPROJEX_REDACTED[manual-secret#1]", secretsOnly.Result.Text, StringComparison.Ordinal);
		Assert.Equal(1, secretsOnly.Snapshot.SecretDetectedCount);
	}

	[Fact]
	public void ManualMarkClasses_AreScopedCountedAndPlaceholderedByEnabledFeatureSet()
	{
		const string secretValue = "manual-secret-value";
		const string privateValue = "manual-private-value";
		var content = $"secret={secretValue}\nprivate={privateValue}";
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("data.txt", content);
		using var session = SecretRedactionSession.CreateWithPrivateData(
			new NeedleDetector("catalog:smart-secrets-v4", "not-present", "unused", RedactionFindingCategory.Secrets),
			new NeedleDetector("private-data-v1", "also-not-present", "unused", RedactionFindingCategory.PrivateData));
		Assert.True(MarkedSecretValueNormalizer.TryCreate(secretValue, out var secret, out _));
		Assert.True(MarkedSecretValueNormalizer.TryCreate(privateValue, out var privateData, out _));
		Assert.True(session.AddSessionMarkedSecret(
			"data.txt",
			"secret=".Length,
			secret,
			ManualRedactionClass.Secret));
		Assert.True(session.AddSessionMarkedSecret(
			"data.txt",
			content.IndexOf(privateValue, StringComparison.Ordinal),
			privateData,
			ManualRedactionClass.PrivateData));

		var secretsOnly = Redact(
			session,
			workspace.Path,
			path,
			content,
			SecretRedactionFeatures.Secrets);
		var privateOnly = Redact(
			session,
			workspace.Path,
			path,
			content,
			SecretRedactionFeatures.PrivateData);
		var combined = Redact(
			session,
			workspace.Path,
			path,
			content,
			SecretRedactionFeatures.Secrets | SecretRedactionFeatures.PrivateData);

		Assert.Contains("DEVPROJEX_REDACTED[manual-secret#1]", secretsOnly.Result.Text, StringComparison.Ordinal);
		Assert.Contains(privateValue, secretsOnly.Result.Text, StringComparison.Ordinal);
		Assert.Equal((1, 0), (secretsOnly.Snapshot.SecretDetectedCount, secretsOnly.Snapshot.PrivateDataDetectedCount));
		Assert.Contains(secretValue, privateOnly.Result.Text, StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_REDACTED[manual-private-data#1]", privateOnly.Result.Text, StringComparison.Ordinal);
		Assert.Equal((0, 1), (privateOnly.Snapshot.SecretDetectedCount, privateOnly.Snapshot.PrivateDataDetectedCount));
		Assert.DoesNotContain(secretValue, combined.Result.Text, StringComparison.Ordinal);
		Assert.DoesNotContain(privateValue, combined.Result.Text, StringComparison.Ordinal);
		Assert.Equal((1, 1), (combined.Snapshot.SecretDetectedCount, combined.Snapshot.PrivateDataDetectedCount));
		Assert.Equal(3, new HashSet<string>(
			[secretsOnly.Snapshot.SelectionKey, privateOnly.Snapshot.SelectionKey, combined.Snapshot.SelectionKey],
			StringComparer.Ordinal).Count);
	}

	private static SecretRedactionSession CreateSessionWithFixedFindings(
		IReadOnlyList<DetectedSecret> findings) =>
		SecretRedactionSession.CreateWithPrivateData(
			new FixedFindingsDetector(
				"catalog:smart-secrets-v4",
				findings.Where(static finding => finding.Category == RedactionFindingCategory.Secrets).ToArray()),
			new FixedFindingsDetector(
				"private-data-v1",
				findings.Where(static finding => finding.Category == RedactionFindingCategory.PrivateData).ToArray()));

	private static DetectedSecret[] CreateRandomFindings(
		Random random,
		string content,
		int iteration,
		int count)
	{
		var findings = new DetectedSecret[count];
		for (var index = 0; index < findings.Length; index++)
		{
			var start = random.Next(0, content.Length - 1);
			var length = random.Next(1, Math.Min(20, content.Length - start) + 1);
			var category = random.Next(2) == 0
				? RedactionFindingCategory.Secrets
				: RedactionFindingCategory.PrivateData;
			findings[index] = new DetectedSecret(
				$"{(category == RedactionFindingCategory.Secrets ? "secret" : "private")}-{iteration}-{index}",
				start,
				length,
				content.Substring(start, length),
				-1_000 + index,
				SecretFindingSource.Detector,
				Category: category);
		}
		return findings;
	}

	private static bool[] GetCoverage(
		int length,
		IReadOnlyList<SecretRedactionScope.ResolvedSecretSegment> segments)
	{
		var coverage = new bool[length];
		foreach (var segment in segments)
			Array.Fill(coverage, true, segment.Start, segment.Length);
		return coverage;
	}

	private static Dictionary<string, string> RevealAllCandidateOccurrenceIds(
		SecretRedactionSession session,
		string projectRoot,
		string path,
		string content,
		SecretRedactionFeatures features,
		int expectedCount)
	{
		var occurrenceIdsByRule = new Dictionary<string, string>(StringComparer.Ordinal);
		for (var pass = 0; pass <= expectedCount; pass++)
		{
			var scan = Redact(session, projectRoot, path, content, features);
			foreach (var span in scan.Result.Spans)
				occurrenceIdsByRule.TryAdd(span.RuleId, span.OccurrenceId);
			if (occurrenceIdsByRule.Count == expectedCount)
				break;
			var activeOccurrenceIds = scan.Result.Spans
				.Where(static span => span.State == SecretPreviewSpanState.Redacted)
				.Select(static span => span.OccurrenceId)
				.Distinct(StringComparer.Ordinal)
				.ToArray();
			if (session.SetKeepAsIs(activeOccurrenceIds, keep: true) == 0)
				break;
		}
		return occurrenceIdsByRule;
	}

	private static int CountOccurrences(string value, string search)
	{
		var count = 0;
		var offset = 0;
		while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
		{
			count++;
			offset += search.Length;
		}
		return count;
	}

	private static void AssertSnapshot(
		SecretRedactionSession session,
		string projectRoot,
		string path,
		string content,
		SecretRedactionFeatures features,
		int expectedSecretCount,
		int expectedPrivateCount)
	{
		var scan = Redact(session, projectRoot, path, content, features);
		Assert.Equal(expectedSecretCount, scan.Snapshot.SecretDetectedCount);
		Assert.Equal(expectedPrivateCount, scan.Snapshot.PrivateDataDetectedCount);
	}

	private static void AssertCountOnlyMatches(
		SecretRedactionSession session,
		string projectRoot,
		string path,
		string content,
		SecretRedactionFeatures features,
		SecretRedactionSnapshot expected)
	{
		var scope = session.BeginOutput(projectRoot, [path], features: features);
		scope.AnalyzeTransformed(
			path,
			content,
			ContentTransformMap.Identity,
			SecretFileMetadata.Capture(path),
			knownFingerprint: null,
			TestContext.Current.CancellationToken);
		var actual = scope.Complete();

		Assert.Equal(expected.DetectedCount, actual.DetectedCount);
		Assert.Equal(expected.RedactedCount, actual.RedactedCount);
		Assert.Equal(expected.SecretDetectedCount, actual.SecretDetectedCount);
		Assert.Equal(expected.SecretRedactedCount, actual.SecretRedactedCount);
		Assert.Equal(expected.PrivateDataDetectedCount, actual.PrivateDataDetectedCount);
		Assert.Equal(expected.PrivateDataRedactedCount, actual.PrivateDataRedactedCount);
		Assert.Equal(
			expected.MarkedSecretCounts?.OrderBy(static pair => pair.Key, StringComparer.Ordinal),
			actual.MarkedSecretCounts?.OrderBy(static pair => pair.Key, StringComparer.Ordinal));
	}

	private static (SecretTextRedactionResult Result, SecretRedactionSnapshot Snapshot) Redact(
		SecretRedactionSession session,
		string projectRoot,
		string path,
		string content,
		SecretRedactionFeatures features)
	{
		var scope = session.BeginOutput(projectRoot, [path], features: features);
		var result = scope.Redact(path, content, TestContext.Current.CancellationToken);
		return (result, scope.Complete());
	}

	private static DetectedSecret CreateFinding(
		string content,
		string value,
		string ruleId,
		RedactionFindingCategory category)
	{
		var start = content.IndexOf(value, StringComparison.Ordinal);
		return new DetectedSecret(ruleId, start, value.Length, value, -100, Category: category);
	}

	private sealed class FixedFindingsDetector(
		string rulesIdentity,
		IReadOnlyList<DetectedSecret> findings) : ISecretDetector
	{
		public string RulesIdentity => rulesIdentity;

		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return findings;
		}
	}

	private sealed class NeedleDetector(
		string rulesIdentity,
		string needle,
		string ruleId,
		RedactionFindingCategory category) : ISecretDetector
	{
		private int _callCount;

		public string RulesIdentity => rulesIdentity;
		public int CallCount => Volatile.Read(ref _callCount);

		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _callCount);
			cancellationToken.ThrowIfCancellationRequested();
			var start = content.IndexOf(needle, StringComparison.Ordinal);
			return start < 0
				? []
				: [new DetectedSecret(ruleId, start, needle.Length, needle, -100, Category: category)];
		}
	}
}
