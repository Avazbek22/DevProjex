using System.Drawing;

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

	[Fact]
	public void CommandInputEscapesPastedControlCharacters()
	{
		var editor = new TerminalTransparentTextEditor { Value = "search first\nsecond" };
		editor.MoveEnd();
		editor.InsertText("\t\u001B");

		Assert.Equal(@"search first\nsecond\t\u001B", editor.Value);
		Assert.DoesNotContain(editor.Value, char.IsControl);
	}

	[Theory]
	[InlineData("abcdefgh", 5, 4)]
	[InlineData("ab界cd", 5, 2)]
	[InlineData("abcdefgh", 1, 8)]
	public void CommandInputKeepsCursorVisibleWithoutSplittingWideRunes(
		string value,
		int width,
		int expectedOffset)
	{
		var editor = new TerminalTransparentTextEditor
		{
			Frame = new Rectangle(0, 0, width, 1),
			Value = value
		};

		editor.MoveEnd();

		Assert.Equal(expectedOffset, editor.ScrollOffset);
	}
}
