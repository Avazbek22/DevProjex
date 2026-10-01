namespace DevProjex.Tests.Unit;

public sealed class GitRepositorySafetyInspectorTests
{
	[Fact]
	public void InterpretUnsafeDriverResult_ExitZeroPreservesCaseDistinctDriverNames()
	{
		var inspection = GitRepositorySafetyInspector.InterpretUnsafeDriverResult(
			new GitInspectionProcessResult(
				0,
				"filter.Foo.smudge\nfilter.foo.smudge\nFILTER.Bar.CLEAN\n" +
				"diff.Q.textconv\ndiff.q.command\ndiff.external\n"));

		Assert.True(inspection.IsComplete);
		Assert.Equal(["Bar", "Foo", "foo"], inspection.CheckoutFilterDrivers);
		Assert.Equal(["Bar"], inspection.UnsafeWorkingTreeDrivers);
		Assert.Equal(["Q", "external", "q"], inspection.ExternalDiffDrivers);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	public void InterpretUnsafeDriverResult_EmptySuccessfulQueryMeansNoDrivers(int exitCode)
	{
		var inspection = GitRepositorySafetyInspector.InterpretUnsafeDriverResult(
			new GitInspectionProcessResult(exitCode, string.Empty));

		Assert.True(inspection.IsComplete);
		Assert.Empty(inspection.CheckoutFilterDrivers);
		Assert.Empty(inspection.UnsafeWorkingTreeDrivers);
		Assert.Empty(inspection.ExternalDiffDrivers);
	}

	[Fact]
	public void InterpretUnsafeDriverResult_ExitOneWithOutputIsUnavailable()
	{
		var inspection = GitRepositorySafetyInspector.InterpretUnsafeDriverResult(
			new GitInspectionProcessResult(1, "unexpected"));

		Assert.False(inspection.IsComplete);
	}

	[Fact]
	public void InterpretUnsafeDriverResult_Exit128IsUnavailable()
	{
		var inspection = GitRepositorySafetyInspector.InterpretUnsafeDriverResult(
			new GitInspectionProcessResult(128, string.Empty));

		Assert.False(inspection.IsComplete);
	}

	[Fact]
	public void InterpretUnsafeDriverResult_NullProcessResultIsUnavailable()
	{
		Assert.False(GitRepositorySafetyInspector.InterpretUnsafeDriverResult(null).IsComplete);
	}

	[Fact]
	public void InterpretUnsafeDriverResult_OutputLimitIsUnavailable()
	{
		var inspection = GitRepositorySafetyInspector.InterpretUnsafeDriverResult(
			new GitInspectionProcessResult(0, "filter.safe.smudge", ExceededOutputLimit: true));

		Assert.False(inspection.IsComplete);
	}
}
