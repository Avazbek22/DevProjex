namespace DevProjex.Tests.Terminal;

public sealed class TerminalTransparentTextEditorTests
{
	[Fact]
	public void CommandInputRejectsContentBeyondThePersistedHistoryLimit()
	{
		var editor = new TerminalTransparentTextEditor
		{
			Value = new string('x', TerminalCommandHistory.MaximumCommandLength + 100)
		};

		Assert.Equal(TerminalCommandHistory.MaximumCommandLength, editor.Value.Length);

		editor.Value = new string('x', TerminalCommandHistory.MaximumCommandLength - 1);
		editor.MoveEnd();
		editor.InsertText("😀tail");

		Assert.Equal(new string('x', TerminalCommandHistory.MaximumCommandLength - 1), editor.Value);
	}
}
