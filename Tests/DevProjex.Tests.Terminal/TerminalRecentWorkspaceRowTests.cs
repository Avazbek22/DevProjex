using DevProjex.Application.Workspaces;
using Terminal.Gui.Text;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalRecentWorkspaceRowTests
{
	[Fact]
	public void ToString_BoundsLongUnicodeColumnsAndKeepsOpenedValueVisible()
	{
		var workspace = new RecentWorkspaceDescriptor(
			RecentWorkspaceKind.Folder,
			@"C:\Projects\非常に長いプロジェクト名",
			@"C:\Projects\非常に長いプロジェクト名",
			"非常に長いプロジェクト名と追加情報",
			"folder:test",
			DateTimeOffset.UnixEpoch);
		var row = new TerminalRecentWorkspaceRow(
			workspace,
			static _ => "Очень длинный тип",
			static _ => "today")
		{
			IsSelected = true
		};

		var rendered = row.ToString();

		Assert.StartsWith("> ", rendered, StringComparison.Ordinal);
		Assert.Contains("...", rendered, StringComparison.Ordinal);
		Assert.EndsWith(" today", rendered, StringComparison.Ordinal);
		Assert.Equal(47, rendered.GetColumns());
		Assert.DoesNotContain(workspace.DisplayName, rendered, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("DevProjex", 28, "DevProjex")]
	[InlineData("Long project identity that exceeds the column", 10, "Long pr...")]
	[InlineData("项目名称很长", 7, "项目...")]
	public void FitToColumns_UsesTerminalCellWidth(
		string value,
		int width,
		string expected)
	{
		var result = TerminalRecentWorkspaceRow.FitToColumns(value, width);

		Assert.Equal(expected, result);
		Assert.True(result.GetColumns() <= width);
	}
}
