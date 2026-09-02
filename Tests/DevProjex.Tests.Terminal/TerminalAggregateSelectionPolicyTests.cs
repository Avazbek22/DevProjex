using DevProjex.Application.Presentation;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalAggregateSelectionPolicyTests
{
	[Fact]
	public void EnablingSelectsEveryPathRule()
	{
		var result = TerminalAggregateSelectionPolicy.ResolveExclusions(enabled: true);

		Assert.Equal(
			ProjectPresentationCatalog.Exclusions
				.Select(static descriptor => descriptor.RequireId())
				.Order(),
			result.Order());
	}

	[Fact]
	public void DisablingClearsEveryPathRule()
	{
		var result = TerminalAggregateSelectionPolicy.ResolveExclusions(enabled: false);

		Assert.Empty(result);
	}
}
