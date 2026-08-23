using DevProjex.Application.Presentation;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalAggregateSelectionPolicyTests
{
	[Theory]
	[InlineData(GitFilteringMode.None, GitFilteringMode.TrackedFilesOnly)]
	[InlineData(GitFilteringMode.RespectGitIgnore, GitFilteringMode.TrackedFilesOnly)]
	public void EnablingSelectsEveryRuleAndKeepsTheCurrentOrPreferredGitMode(
		GitFilteringMode current,
		GitFilteringMode preferred)
	{
		var result = TerminalAggregateSelectionPolicy.ResolveExclusions(
			enabled: true,
			current,
			preferred);

		Assert.Equal(
			current == GitFilteringMode.None ? preferred : current,
			result.Mode);
		Assert.Equal(
			ProjectPresentationCatalog.Exclusions
				.Select(static descriptor => descriptor.RequireId())
				.Order(),
			result.Exclusions.Order());
	}

	[Fact]
	public void DisablingClearsGitModeAndEveryRule()
	{
		var result = TerminalAggregateSelectionPolicy.ResolveExclusions(
			enabled: false,
			GitFilteringMode.TrackedFilesOnly,
			GitFilteringMode.RespectGitIgnore);

		Assert.Equal(GitFilteringMode.None, result.Mode);
		Assert.Empty(result.Exclusions);
	}
}
