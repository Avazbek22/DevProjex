using DevProjex.Application.Ranking;
using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpPackingIdentityTests
{
	[Fact]
	public async Task ChangedRankingSourceIsRejectedBeforePackingContinues()
	{
		using var workspace = new TemporaryDirectory();
		var path = workspace.CreateFile("Source.cs", "before\n");
		var version = await RankingSourceVersion.CaptureAsync(path, TestContext.Current.CancellationToken);
		var report = new ImportanceRankingReport(
			ImportanceRankingService.AlgorithmId,
			[],
			[],
			0,
			0,
			0,
			0,
			200,
			0,
			ProjectGitHistoryUnavailableReason.NotRepository,
			true,
			ImportanceRankingService.GraphVariant)
		{
			SourceVersions = new Dictionary<string, RankingSourceVersion>(PathComparer.Default)
			{
				[Path.GetFullPath(path)] = version
			}
		};
		File.WriteAllText(path, "after\n");

		var exception = await Assert.ThrowsAsync<McpToolException>(() =>
			McpProjectService.EnsureRankingSourcesCurrentAsync(
				report,
				[path],
				TestContext.Current.CancellationToken));

		Assert.Equal(McpErrorCodes.ProjectUnavailable, exception.Code);
		Assert.Equal(
			$"{McpErrorCodes.ProjectUnavailable}: selection changed during packing; retry",
			exception.Message);
	}
}
