namespace DevProjex.Tests.Integration;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GitProcessEnvironmentTestCollection
{
	public const string Name = "GitProcessEnvironment";
}

[Collection(GitProcessEnvironmentTestCollection.Name)]
public sealed class GitProcessEnvironmentIntegrationTests
{
	[Fact]
	public async Task RepositoryCommandsIgnoreInheritedRepositoryOverrides()
	{
		if (!await SharedGitRepositories.IsGitAvailableAsync())
			return;

		await using var source = await GitTestRepository.CreateAsync(
			cancellationToken: TestContext.Current.CancellationToken);
		using var temporary = new TemporaryDirectory();
		var target = Path.Combine(temporary.Path, "target");
		var previousGitDirectory = Environment.GetEnvironmentVariable("GIT_DIR");
		var previousWorkTree = Environment.GetEnvironmentVariable("GIT_WORK_TREE");
		try
		{
			Environment.SetEnvironmentVariable("GIT_DIR", Path.Combine(temporary.Path, "wrong.git"));
			Environment.SetEnvironmentVariable("GIT_WORK_TREE", Path.Combine(temporary.Path, "wrong-tree"));
			var service = new GitRepositoryService(allowFileTransportForTests: true);

			var cloned = await service.CloneAsync(
				source.RepositoryUrl,
				target,
				cancellationToken: TestContext.Current.CancellationToken);
			var branch = await service.GetCurrentBranchAsync(
				target,
				cancellationToken: TestContext.Current.CancellationToken);

			Assert.True(cloned.Success, cloned.ErrorMessage);
			Assert.Equal(source.DefaultBranchName, branch);
		}
		finally
		{
			Environment.SetEnvironmentVariable("GIT_DIR", previousGitDirectory);
			Environment.SetEnvironmentVariable("GIT_WORK_TREE", previousWorkTree);
		}
	}
}
