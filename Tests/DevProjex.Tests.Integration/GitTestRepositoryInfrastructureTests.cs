namespace DevProjex.Tests.Integration;

[Collection(GitNetworkTestCollection.Name)]
public sealed class GitTestRepositoryInfrastructureTests
{
	[Fact]
	public async Task CreateAsync_IsolatesBareStorageAndPublishesEveryBranch()
	{
		var gitService = new GitRepositoryService();
		if (!await gitService.IsGitAvailableAsync(TestContext.Current.CancellationToken))
			return;

		await using var repository = await GitTestRepository.CreateAsync(
			repositoryName: "Infrastructure-Probe",
			cancellationToken: TestContext.Current.CancellationToken);
		var isolatedRoot = Path.GetFullPath(Path.Combine(
			Path.GetTempPath(),
			"DevProjex",
			"GitTestRepositories"));
		var barePath = Path.GetFullPath(repository.BareRepositoryPath);
		var isolatedPrefix = Path.TrimEndingDirectorySeparator(isolatedRoot) + Path.DirectorySeparatorChar;

		Assert.StartsWith(isolatedPrefix, barePath, PathComparer.Comparison);
		Assert.True(Directory.Exists(Path.Combine(barePath, "objects")));
		Assert.True(Directory.Exists(Path.Combine(barePath, "refs", "heads")));

		foreach (var branchName in new[]
		         {
			         repository.DefaultBranchName,
			         repository.FeatureBranchName,
			         repository.ReleaseBranchName
		         })
		{
			Assert.NotEmpty(await repository.GetBranchHeadAsync(
				branchName,
				TestContext.Current.CancellationToken));
		}
	}
}
