using DevProjex.Application.Diagnostics;
using DevProjex.Application.Compression;
using DevProjex.Application.Secrets;
using DevProjex.Application.Services;
using DevProjex.Infrastructure.Secrets;

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
		var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());
		using var measuredSession = new SecretRedactionSession(new GitleaksSecretDetector());
		using var diagnostics = ContentPipelineDiagnostics.BeginMeasurement();

		await using var measured = await preparer.MeasureAsync(
			new ContentTransformationContext(
				Compression: null,
				new SecretRedactionContext(root, measuredSession)),
			[path],
			captureEffectiveFindings: true,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(0, diagnostics.Capture().PreparedWriteBytes);
		Assert.Equal(0, diagnostics.Capture().PreparedReadBytes);
		Assert.Single(measured.TransformedFileMetrics);
		Assert.Single(measured.GetEffectiveFindings());

		using var materializedSession = new SecretRedactionSession(new GitleaksSecretDetector());
		await using var materialized = await preparer.PrepareAsync(
			new ContentTransformationContext(
				Compression: null,
				new SecretRedactionContext(root, materializedSession)),
			[path],
			captureEffectiveFindings: true,
			TestContext.Current.CancellationToken);
		var expected = await ProjectContentMetricsCalculator.CalculateAsync(
			preparer.CreatePreparedAnalyzer(materialized),
			[path],
			TestContext.Current.CancellationToken);

		Assert.Equal(expected, measured.GetTransformedMetrics());
		Assert.Equal(materialized.GetEffectiveFindings(), measured.GetEffectiveFindings());
	}
}
