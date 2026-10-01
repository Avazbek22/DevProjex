using Terminal.Gui.Text;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalWelcomeActionRowTests
{
	private const string SharedParent =
		@"C:\Users\developer\source\repos\customer-workspaces\backend-services";

	[Fact]
	public void ToString_ShortensRecentProjectPathFromTheStartToKeepTheFolderName()
	{
		var row = new TerminalWelcomeActionRow(CreateRecentProject($@"{SharedParent}\billing-api", 1))
		{
			IsSelected = true,
			Width = 40
		};

		var rendered = row.ToString();

		Assert.StartsWith("> [1] ...", rendered, StringComparison.Ordinal);
		Assert.EndsWith(@"\billing-api", rendered, StringComparison.Ordinal);
		Assert.Equal(40, rendered.GetColumns());
	}

	[Fact]
	public void ToString_KeepsRecentProjectsUnderOneParentDistinguishable()
	{
		var first = new TerminalWelcomeActionRow(CreateRecentProject($@"{SharedParent}\billing-api", 1))
		{
			Width = 40
		};
		var second = new TerminalWelcomeActionRow(CreateRecentProject($@"{SharedParent}\billing-web", 2))
		{
			Width = 40
		};

		Assert.NotEqual(first.ToString()[6..], second.ToString()[6..]);
	}

	[Fact]
	public void ToString_ShowsAPathThatFitsAndOtherActionsUnchanged()
	{
		var shortPath = new TerminalWelcomeActionRow(CreateRecentProject(@"C:\src\app", 3))
		{
			Width = 40
		};
		var browse = new TerminalWelcomeActionRow(new TerminalWelcomeAction(
			TerminalWelcomeActionKind.BrowseFolder,
			"Browse folder",
			"Select a folder."))
		{
			Width = 10
		};

		Assert.Equal(@"  [3] C:\src\app", shortPath.ToString());
		Assert.Equal("  Browse folder", browse.ToString());
	}

	[Fact]
	public void ToString_WithoutAWidthShowsTheFullTitle()
	{
		var path = $@"{SharedParent}\billing-api";
		var row = new TerminalWelcomeActionRow(CreateRecentProject(path, 1));

		Assert.Equal($"  [1] {path}", row.ToString());
	}

	private static TerminalWelcomeAction CreateRecentProject(string path, int number) =>
		new(
			TerminalWelcomeActionKind.RecentProject,
			path,
			"Open this recent project.",
			path,
			number);
}
