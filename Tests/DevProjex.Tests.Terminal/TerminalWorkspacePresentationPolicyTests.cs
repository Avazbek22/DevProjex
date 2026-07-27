using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Tui;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalWorkspacePresentationPolicyTests
{
	[Theory]
	[InlineData(TerminalColorMode.Auto, false, false, false)]
	[InlineData(TerminalColorMode.Auto, false, true, true)]
	[InlineData(TerminalColorMode.Always, false, true, false)]
	[InlineData(TerminalColorMode.Never, false, false, true)]
	[InlineData(TerminalColorMode.Always, true, false, true)]
	public void Resolve_UsesExpectedMonochromePolicy(
		TerminalColorMode color,
		bool plain,
		bool noColor,
		bool expectedMonochrome)
	{
		var environment = new TestTerminalEnvironment
		{
			IsNoColor = noColor
		};

		var result = TerminalWorkspacePresentationPolicy.Resolve(
			color,
			plain,
			environment);

		Assert.Equal(expectedMonochrome, result.UseMonochromeScheme);
		Assert.Equal(
			expectedMonochrome
				? TerminalWorkspacePresentationPolicy.MonochromeSchemeName
				: null,
			result.SchemeName);
	}
}
