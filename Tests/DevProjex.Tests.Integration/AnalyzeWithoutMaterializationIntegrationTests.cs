using DevProjex.Application.Diagnostics;
using DevProjex.Application.Compression;
using DevProjex.Application.Secrets;
using DevProjex.Application.Services;
using DevProjex.Infrastructure.Secrets;
using System.Collections.Concurrent;

namespace DevProjex.Tests.Integration;

public sealed class AnalyzeWithoutMaterializationIntegrationTests
{
	[Fact]
	public async Task MeasureAsync_MatchesPreparedOutputMetricsWithoutWritingPreparedFiles()
	{
		using var temporary = new TemporaryDirectory();
		var root = temporary.CreateDirectory("project");
		var token = "ghp_" + "a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL";
		var path = temporary.CreateFile(
			"project/source.cs",
			$"internal class Source\r\n{{\r\n\tprivate const string Token = \"{token}\";\r\n}}\r\n");
		var detectorExcludedPath = temporary.CreateFile(
			"project/detector-excluded.txt",
			"plain text excluded by detector policy\r\n");
		var packageLockPath = temporary.CreateFile(
			"project/package-lock.json",
			"{\n  \"name\": \"fixture\",\n  \"lockfileVersion\": 3\n}\n");
		var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());
		var measuredDetector = new SelectiveCountingDetector("detector-excluded.txt");
		using var measuredSession = new SecretRedactionSession(measuredDetector);
		using var diagnostics = ContentPipelineDiagnostics.BeginMeasurement();

		await using var measured = await preparer.MeasureAsync(
			new ContentTransformationContext(
				Compression: null,
				new SecretRedactionContext(root, measuredSession)),
			[path, detectorExcludedPath, packageLockPath],
			captureEffectiveFindings: true,
			cancellationToken: TestContext.Current.CancellationToken);

		var measuredDiagnostics = diagnostics.Capture();
		Assert.Equal(0, measuredDiagnostics.PreparedWriteBytes);
		Assert.Equal(0, measuredDiagnostics.PreparedReadBytes);
		// Selected lock files still reach fixed-prefix provider rules; only heuristic rules retain path allowlists.
		Assert.Equal(2, measuredDiagnostics.MeasurementPasses);
		Assert.Equal(3, measured.TransformedFileMetrics.Count);
		Assert.Single(measured.GetEffectiveFindings());
		Assert.DoesNotContain("detector-excluded.txt", measuredDetector.InspectedPaths);
		Assert.Contains("package-lock.json", measuredDetector.InspectedPaths);

		using var materializedSession = new SecretRedactionSession(
			new SelectiveCountingDetector("detector-excluded.txt"));
		await using var materialized = await preparer.PrepareAsync(
			new ContentTransformationContext(
				Compression: null,
				new SecretRedactionContext(root, materializedSession)),
			[path, detectorExcludedPath, packageLockPath],
			captureEffectiveFindings: true,
			TestContext.Current.CancellationToken);
		var expected = await ProjectContentMetricsCalculator.CalculateAsync(
			preparer.CreatePreparedAnalyzer(materialized),
			[path, detectorExcludedPath, packageLockPath],
			TestContext.Current.CancellationToken);

		Assert.Equal(expected, measured.GetTransformedMetrics());
		Assert.Equal(materialized.GetEffectiveFindings(), measured.GetEffectiveFindings());
	}

	[Fact]
	public async Task PrepareAsync_CapturesTransformedMetricsDuringThePreparedWrite()
	{
		using var temporary = new TemporaryDirectory();
		var root = temporary.CreateDirectory("project");
		var token = "ghp_" + "a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL";
		var path = temporary.CreateFile("project/source.cs", $"const string Token = \"{token}\";\r\n");
		using var session = new SecretRedactionSession(new GitleaksSecretDetector());
		using var diagnostics = ContentPipelineDiagnostics.BeginMeasurement();

		await using var prepared = await new SecretRedactionOutputPreparer(new FileContentAnalyzer())
			.PrepareAsync(
				new ContentTransformationContext(null, new SecretRedactionContext(root, session)),
				[path],
				captureEffectiveFindings: true,
				captureTransformedMetrics: true,
				TestContext.Current.CancellationToken);
		var snapshot = diagnostics.Capture();

		Assert.Single(prepared.TransformedFileMetrics);
		Assert.True(snapshot.PreparedWriteBytes > 0);
		Assert.Equal(0, snapshot.MeasurementPasses);
		Assert.Equal(1, snapshot.PreparedFilesMaterialized);
	}

	private sealed class SelectiveCountingDetector(string excludedFileName) : ISecretDetector
	{
		private readonly GitleaksSecretDetector inner = new();
		private readonly ConcurrentDictionary<string, byte> inspectedPaths = new(StringComparer.Ordinal);

		public IEnumerable<string> InspectedPaths => inspectedPaths.Keys;
		public string RulesIdentity => inner.RulesIdentity + ":selective-fixture";

		public bool ShouldInspectPath(string repositoryRelativePath) =>
			!repositoryRelativePath.EndsWith(excludedFileName, StringComparison.Ordinal) &&
			inner.ShouldInspectPath(repositoryRelativePath);

		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default) =>
			Detect(repositoryRelativePath, content.AsSpan(), cancellationToken);

		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			ReadOnlySpan<char> content,
			CancellationToken cancellationToken = default)
		{
			inspectedPaths.TryAdd(repositoryRelativePath, 0);
			return inner.Detect(repositoryRelativePath, content, cancellationToken);
		}
	}
}
