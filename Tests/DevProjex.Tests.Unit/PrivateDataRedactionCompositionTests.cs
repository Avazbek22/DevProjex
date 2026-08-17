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

		Assert.True(session.ToggleKeepAsIs(privateSpan.OccurrenceId));
		var fullyKept = Redact(session, workspace.Path, path, content, features);
		Assert.Equal(content, fullyKept.Result.Text);
		var keptSpan = Assert.Single(fullyKept.Result.Spans);
		Assert.Equal(SecretPreviewSpanState.KeptAsIs, keptSpan.State);
		Assert.Equal((1, 0), (
			fullyKept.Snapshot.SecretDetectedCount,
			fullyKept.Snapshot.PrivateDataDetectedCount));
		Assert.Equal(0, fullyKept.Snapshot.RedactedCount);
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
	[InlineData("AASECRETBB", "SECRET", "AASECRETBB", 1, 2)]
	[InlineData("AASECRETBB", "AASECRETBB", "SECRET", 1, 0)]
	[InlineData("AASECRETBB", "AASECRET", "SECRETBB", 1, 1)]
	public void CrossCategoryOverlap_PreservesOnlyPrivateResidualSegments(
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
		Assert.Equal(expectedSecretCount + expectedPrivateCount, scan.Result.Spans.Count);
		Assert.Equal(expectedSecretCount, scan.Result.Spans.Count(static span => span.RuleId == "secret-rule"));
		Assert.Equal(expectedPrivateCount, scan.Result.Spans.Count(static span => span.RuleId == "private-rule"));
		Assert.DoesNotContain(secretValue, scan.Result.Text, StringComparison.Ordinal);
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
