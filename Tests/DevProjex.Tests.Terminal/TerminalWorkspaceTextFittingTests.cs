using System.Drawing;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalWorkspaceTextFittingTests
{
	[Theory]
	[InlineData(
		"file:///Users/runner/work/_temp/session/CombatRepository",
		40,
		"file:///",
		"/CombatRepository")]
	[InlineData(
		"https://github.example.com/organization/projects/DevProjex",
		45,
		"https://github.example.com/",
		"/DevProjex")]
	public void FitPathToWidth_PreservesRepositorySourceIdentity(
		string value,
		int width,
		string expectedPrefix,
		string expectedSuffix)
	{
		var result = TerminalWorkspaceSession.FitPathToWidth(value, width);

		Assert.StartsWith(expectedPrefix, result, StringComparison.Ordinal);
		Assert.EndsWith(expectedSuffix, result, StringComparison.Ordinal);
		Assert.Contains("...", result, StringComparison.Ordinal);
		Assert.Equal(width, result.Length);
	}

	[Fact]
	public void FitPathToWidth_DoesNotPresentLocalWindowsPathAsFileUri()
	{
		const string value =
			@"C:\Users\developer\RiderProjects\organization\DevProjex";

		var result = TerminalWorkspaceSession.FitPathToWidth(value, 32);

		Assert.StartsWith("...", result, StringComparison.Ordinal);
		Assert.EndsWith(@"\DevProjex", result, StringComparison.Ordinal);
		Assert.DoesNotContain("file:///", result, StringComparison.Ordinal);
		Assert.Equal(32, result.Length);
	}

	[Theory]
	[InlineData(40, 10)]
	[InlineData(45, 12)]
	[InlineData(66, 15)]
	[InlineData(120, 12)]
	public void TooSmallHintStaysInsideTheScreenWithTheRequiredSize(int columns, int rows)
	{
		const string hint =
			"O terminal é demasiado pequeno. Redimensione-o para, pelo menos, 60 × 20.";
		using var screen = new View { Frame = new Rectangle(0, 0, columns, rows) };
		var label = TerminalWorkspaceSession.CreateTooSmallLabel(hint);
		label.Visible = true;
		screen.Add(label);

		screen.Layout();

		var lines = label.TextFormatter.GetLines();
		Assert.InRange(label.Frame.X, 0, columns);
		Assert.InRange(label.Frame.Right, 0, columns);
		Assert.InRange(label.Frame.Y, 0, rows);
		Assert.InRange(label.Frame.Bottom, 0, rows);
		Assert.InRange(lines.Count, 1, label.Frame.Height);
		Assert.All(lines, line => Assert.True(line.GetColumns() <= label.Frame.Width, line));
		Assert.Equal(hint, string.Join(' ', lines));
	}

	[Theory]
	[InlineData("Export completed: {0}")]
	[InlineData("Экспорт завершён: {0}")]
	[InlineData("Agent journal exported: {0}")]
	public void FormatStatusPath_KeepsTheWrittenFileNameInsideOneStatusRow(string format)
	{
		const int columns = 78;
		var path = Path.Combine(
			@"C:\Users\developer\AppData\Local\Temp",
			string.Join('\\', Enumerable.Repeat("deeply-nested-export-folder", 4)),
			"r one.md");

		var result = TerminalWorkspaceSession.FormatStatusPath(format, path, columns);

		Assert.StartsWith(format.Replace("{0}", "...", StringComparison.Ordinal), result, StringComparison.Ordinal);
		Assert.EndsWith(@"\r one.md", result, StringComparison.Ordinal);
		Assert.Equal(columns, result.Length);
	}
}
