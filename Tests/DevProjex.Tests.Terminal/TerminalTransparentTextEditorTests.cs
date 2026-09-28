using System.Drawing;
using System.Reflection;
using Terminal.Gui.Input;

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

	[Fact]
	public void PasteInsertsTheCopiedLineAtTheCursor()
	{
		var editor = new TerminalTransparentTextEditor { Value = "open  --profile x" };
		editor.InsertionPoint = 5;

		var sanitized = Assert.IsType<string>(InvokeProtected(
			editor,
			"OnSanitizingPaste",
			"C:\\work\\my repo\r\n"));
		var consumed = Assert.IsType<bool>(InvokeProtected(editor, "OnPaste", sanitized));

		Assert.True(consumed);
		Assert.Equal("open C:\\work\\my repo --profile x", editor.Value);
		Assert.Equal(20, editor.InsertionPoint);
	}

	[Fact]
	public void KeysThatResolveToControlCharactersDoNotEditTheCommand()
	{
		var editor = new TerminalTransparentTextEditor { Value = "vi" };
		editor.MoveEnd();

		editor.NewKeyDownEvent(Key.Tab.WithShift);
		editor.NewKeyDownEvent(Key.Enter.WithShift);
		editor.NewKeyDownEvent(new Key('w'));

		Assert.Equal("viw", editor.Value);
	}

	[Theory]
	[InlineData("abcdefgh", 5, 4)]
	[InlineData("ab界cd", 5, 2)]
	[InlineData("a😀界bc", 5, 2)]
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

	private static object? InvokeProtected(
		TerminalTransparentTextEditor editor,
		string methodName,
		string argument)
	{
		var method = typeof(TerminalTransparentTextEditor).GetMethod(
			methodName,
			BindingFlags.Instance | BindingFlags.NonPublic,
			[typeof(string)]);
		Assert.NotNull(method);
		return method.Invoke(editor, [argument]);
	}
}
