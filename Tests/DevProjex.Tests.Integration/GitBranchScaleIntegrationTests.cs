namespace DevProjex.Tests.Integration;

[Collection(GitNetworkTestCollection.Name)]
public sealed class GitBranchScaleIntegrationTests
{
	[Theory]
	[InlineData(1_000)]
	[InlineData(10_000)]
	public async Task LargeRemoteBranchCatalogIsStreamedWithoutFallingBackToTheCurrentBranch(int branchCount)
	{
		if (!GitRuntime.VersionDisplay.StartsWith("git version ", StringComparison.OrdinalIgnoreCase))
			Assert.Skip("Git is not available on this system.");

		await using var source = await GitTestRepository.CreateAsync(
			$"branch-scale-{branchCount}",
			cancellationToken: TestContext.Current.CancellationToken);
		await AddBranchesAsync(source, branchCount - 3, TestContext.Current.CancellationToken);
		using var workspace = new TemporaryDirectory();
		var target = Path.Combine(workspace.Path, "clone");
		using var service = new GitRepositoryService(allowFileTransportForTests: true);
		var cloned = await service.CloneAsync(
			source.RepositoryUrl,
			target,
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.True(cloned.Success, cloned.ErrorMessage);

		var result = await service.GetBranchesWithStatusAsync(
			target,
			TestContext.Current.CancellationToken);

		Assert.False(result.IsIncomplete);
		Assert.Equal(branchCount, result.Branches.Count);
		Assert.Contains(result.Branches, branch => branch.Name.StartsWith("feature/scale-", StringComparison.Ordinal));
		Assert.Equal(result.Branches.Select(static branch => branch.Name).Distinct(StringComparer.Ordinal).Count(), branchCount);
	}

	private static async Task AddBranchesAsync(
		GitTestRepository repository,
		int additionalBranches,
		CancellationToken cancellationToken)
	{
		if (additionalBranches <= 0)
			return;
		var head = await repository.GetBranchHeadAsync(repository.DefaultBranchName, cancellationToken);
		var startInfo = new ProcessStartInfo
		{
			FileName = OperatingSystem.IsWindows() ? "git.exe" : "git",
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		startInfo.ArgumentList.Add($"--git-dir={repository.BareRepositoryPath}");
		startInfo.ArgumentList.Add("update-ref");
		startInfo.ArgumentList.Add("--stdin");
		using var process = new Process { StartInfo = startInfo };
		process.Start();
		process.StandardInput.NewLine = "\n";
		var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
		var error = process.StandardError.ReadToEndAsync(cancellationToken);
		Exception? writeFailure = null;
		try
		{
			for (var index = 0; index < additionalBranches; index++)
			{
				var suffix = new string((char)('a' + index % 26), 32);
				await process.StandardInput.WriteLineAsync(
					$"create refs/heads/feature/scale-{index:D5}-{suffix} {head}".AsMemory(),
					cancellationToken);
			}
		}
		catch (IOException exception)
		{
			writeFailure = exception;
		}
		process.StandardInput.Close();
		await process.WaitForExitAsync(cancellationToken);
		var standardOutput = await output;
		var standardError = await error;
		Assert.True(
			process.ExitCode == 0 && writeFailure is null,
			$"git update-ref failed: {writeFailure?.Message}{Environment.NewLine}{standardOutput}{Environment.NewLine}{standardError}");
	}
}
