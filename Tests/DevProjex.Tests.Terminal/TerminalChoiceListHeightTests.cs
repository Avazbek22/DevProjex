namespace DevProjex.Tests.Terminal;

public sealed class TerminalChoiceListHeightTests
{
	[Theory]
	[InlineData(3, 12, 3)]
	[InlineData(11, 20, 11)]
	[InlineData(30, 20, 11)]
	[InlineData(30, 5, 1)]
	public void ChoiceListStaysInsideTheDialog(int itemCount, int dialogHeight, int expectedHeight)
	{
		Assert.Equal(
			expectedHeight,
			TerminalWorkspaceSession.ResolveChoiceListHeight(itemCount, dialogHeight));
	}
}
