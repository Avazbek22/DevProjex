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
	public void Overlap_SecretWinsAndKeepAsIsDoesNotRevivePrivateFinding()
	{
		const string content = "Cookie: client=93.184." + "216.34";
		const string secretValue = "client=93.184." + "216.34";
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("request.http", content);
		var secrets = new NeedleDetector(
			"catalog:smart-secrets-v4",
			secretValue,
			"http-cookie",
			RedactionFindingCategory.Secrets);
		using var session = SecretRedactionSession.CreateWithPrivateData(secrets, new PrivateDataDetector());
		var features = SecretRedactionFeatures.Secrets | SecretRedactionFeatures.PrivateData;

		var firstScope = session.BeginOutput(workspace.Path, [path], features: features);
		var first = firstScope.Redact(path, content, TestContext.Current.CancellationToken);
		var firstSnapshot = firstScope.Complete();
		var secretSpan = Assert.Single(first.Spans);
		Assert.Equal("http-cookie", secretSpan.RuleId);
		Assert.DoesNotContain("DEVPROJEX_REDACTED[ipv4", first.Text, StringComparison.Ordinal);
		Assert.Equal(1, firstSnapshot.SecretDetectedCount);
		Assert.Equal(0, firstSnapshot.PrivateDataDetectedCount);

		Assert.True(session.ToggleKeepAsIs(secretSpan.OccurrenceId));
		var secondScope = session.BeginOutput(workspace.Path, [path], features: features);
		var second = secondScope.Redact(path, content, TestContext.Current.CancellationToken);
		var secondSnapshot = secondScope.Complete();

		Assert.Equal(content, second.Text);
		var kept = Assert.Single(second.Spans);
		Assert.Equal("http-cookie", kept.RuleId);
		Assert.Equal(SecretPreviewSpanState.KeptAsIs, kept.State);
		Assert.Equal(1, secondSnapshot.SecretDetectedCount);
		Assert.Equal(0, secondSnapshot.PrivateDataDetectedCount);
		Assert.Equal(0, secondSnapshot.RedactedCount);
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
		const string firstContent = "source=C:\\Users\\" + "alice\\first";
		const string secondContent = "source=/home/" + "alice/second";
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
