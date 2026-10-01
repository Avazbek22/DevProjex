using DevProjex.Infrastructure.Git;

namespace DevProjex.Tests.Unit;

public sealed class GitBranchLineCollectorTests
{
	[Fact]
	public void RecordLimitKeepsTheBoundedPrefixAndReportsIncomplete()
	{
		var collector = new GitBranchLineCollector(2, 32, 64);

		collector.Add(new GitProcessLineFrame("first", ExceededLimit: false));
		collector.Add(new GitProcessLineFrame("second", ExceededLimit: false));
		collector.Add(new GitProcessLineFrame("third", ExceededLimit: false));

		Assert.Equal(["first", "second"], collector.Lines);
		Assert.True(collector.IsIncomplete);
	}

	[Fact]
	public void OversizedRecordIsNotPublishedAsAValidBranch()
	{
		var collector = new GitBranchLineCollector(10, 5, 100);

		collector.Add(new GitProcessLineFrame("trunc", ExceededLimit: true));
		collector.Add(new GitProcessLineFrame("valid", ExceededLimit: false));

		Assert.Equal(["valid"], collector.Lines);
		Assert.True(collector.IsIncomplete);
	}
}
