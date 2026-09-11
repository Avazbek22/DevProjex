namespace DevProjex.Tests.Unit;

public sealed class RepositoryTransportPolicyTests
{
	private const string RetiredPolicyVariable = "DEVPROJEX_INTERNAL_TEST_ALLOW_FILE_GIT";

	private static readonly string LocalRepositoryUrl = new Uri(
		Path.Combine(Path.GetTempPath(), "devprojex-transport-fixture.git")).AbsoluteUri;

	[Fact]
	public void ShippedPolicyRejectsALocalFileUrl() =>
		Assert.False(RepositoryUrlUtility.IsSupportedCloneSource(LocalRepositoryUrl));

	[Fact]
	public void TheRetiredEnvironmentVariableDoesNotWidenTheShippedPolicy()
	{
		var previous = Environment.GetEnvironmentVariable(RetiredPolicyVariable);
		Environment.SetEnvironmentVariable(RetiredPolicyVariable, "1");
		try
		{
			Assert.False(RepositoryUrlUtility.IsSupportedCloneSource(LocalRepositoryUrl));
		}
		finally
		{
			Environment.SetEnvironmentVariable(RetiredPolicyVariable, previous);
		}
	}

	[Fact]
	public void AnArmedScopeAllowsALocalFileUrlAndDisposalRestoresTheShippedPolicy()
	{
		Assert.False(RepositoryUrlUtility.IsSupportedCloneSource(LocalRepositoryUrl));
		using (RepositoryTransportPolicy.AllowLocalFileTransport())
			Assert.True(RepositoryUrlUtility.IsSupportedCloneSource(LocalRepositoryUrl));
		Assert.False(RepositoryUrlUtility.IsSupportedCloneSource(LocalRepositoryUrl));
	}

	[Fact]
	public void NestedScopesRestoreTheOuterStateIndependently()
	{
		using (RepositoryTransportPolicy.AllowLocalFileTransport())
		{
			using (RepositoryTransportPolicy.AllowLocalFileTransport())
				Assert.True(RepositoryUrlUtility.IsSupportedCloneSource(LocalRepositoryUrl));

			Assert.True(RepositoryUrlUtility.IsSupportedCloneSource(LocalRepositoryUrl));
		}

		Assert.False(RepositoryUrlUtility.IsSupportedCloneSource(LocalRepositoryUrl));
	}

	[Fact]
	public async Task AnOperationStartedOutsideTheScopeKeepsTheShippedPolicy()
	{
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var outside = Task.Run(async () =>
		{
			await release.Task;
			observed.SetResult(RepositoryUrlUtility.IsSupportedCloneSource(LocalRepositoryUrl));
		});

		using (RepositoryTransportPolicy.AllowLocalFileTransport())
		{
			Assert.True(RepositoryUrlUtility.IsSupportedCloneSource(LocalRepositoryUrl));
			release.SetResult();
			Assert.False(await observed.Task);
		}

		await outside;
	}

	[Theory]
	[InlineData("https://example.test/team/repository.git")]
	[InlineData("ssh://git@example.test/team/repository.git")]
	[InlineData("git@example.test:team/repository.git")]
	public void NetworkTransportsRemainSupportedWithoutAScope(string source) =>
		Assert.True(RepositoryUrlUtility.IsSupportedCloneSource(source));

	[Theory]
	[InlineData("http://example.test/team/repository.git")]
	[InlineData("git://example.test/team/repository.git")]
	public void UnsupportedNetworkTransportsStayRejectedInsideAnArmedScope(string source)
	{
		using var policy = RepositoryTransportPolicy.AllowLocalFileTransport();
		Assert.False(RepositoryUrlUtility.IsSupportedCloneSource(source));
	}

	[Fact]
	public void AnExistingLocalDirectoryPathRemainsSupportedWithoutAScope()
	{
		using var workspace = new TemporaryDirectory();
		Assert.True(RepositoryUrlUtility.IsSupportedCloneSource(workspace.Path));
	}
}
