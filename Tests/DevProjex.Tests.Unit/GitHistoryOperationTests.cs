using DevProjex.Infrastructure.Git;

namespace DevProjex.Tests.Unit;

public sealed class GitHistoryOperationTests
{
	[Fact]
	public void ShallowRepositoryProbeIsPinnedToLocalReadRevParse()
	{
		var startInfo = GitProcessStartInfoFactory.Create(
			Path.GetFullPath("repository"),
			GitProcessOperation.ReadShallowRepositoryState());
		var arguments = startInfo.ArgumentList.ToArray();

		Assert.Equal("--no-pager", arguments[0]);
		Assert.Contains("core.fsmonitor=false", arguments);
		Assert.Equal(
			["rev-parse", "--is-shallow-repository"],
			arguments[^2..]);
		Assert.Equal("", startInfo.Environment["GIT_ALLOW_PROTOCOL"]);
		Assert.Equal("1", startInfo.Environment["GIT_NO_LAZY_FETCH"]);
	}
}
