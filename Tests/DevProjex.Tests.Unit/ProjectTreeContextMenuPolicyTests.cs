using DevProjex.Application.Services;

namespace DevProjex.Tests.Unit;

public sealed class ProjectTreeContextMenuPolicyTests
{
	[Fact]
	public void Build_File_ReturnsExactCommandOrder()
	{
		var entries = ProjectTreeContextMenuPolicy.Build(
			isDirectory: false,
			isExpanded: false,
			allowContentAndSelection: true,
			showSelectOnly: true);

		Assert.Equal(
			[
				"OpenInFileManager",
				"-",
				"CopyFullPath",
				"CopyRelativePath",
				"CopyContent",
				"-",
				"SelectOnly"
			],
			Describe(entries));
	}

	[Theory]
	[InlineData(false, "ExpandBranch")]
	[InlineData(true, "CollapseBranch")]
	public void Build_FolderOrRoot_ReturnsExactCommandOrder(
		bool isExpanded,
		string branchCommand)
	{
		var entries = ProjectTreeContextMenuPolicy.Build(
			isDirectory: true,
			isExpanded,
			allowContentAndSelection: true,
			showSelectOnly: true);

		Assert.Equal(
			[
				"OpenInFileManager",
				"-",
				"CopyFullPath",
				"CopyRelativePath",
				"-",
				"SelectOnly",
				branchCommand
			],
			Describe(entries));
	}

	[Fact]
	public void Build_DuringProjectLoad_DisablesOnlyContentAndSelection()
	{
		var entries = ProjectTreeContextMenuPolicy.Build(
			isDirectory: false,
			isExpanded: false,
			allowContentAndSelection: false,
			showSelectOnly: true);

		Assert.False(Find(entries, ProjectTreeContextMenuCommand.CopyContent).IsEnabled);
		Assert.False(Find(entries, ProjectTreeContextMenuCommand.SelectOnly).IsEnabled);
		Assert.True(Find(entries, ProjectTreeContextMenuCommand.OpenInFileManager).IsEnabled);
		Assert.True(Find(entries, ProjectTreeContextMenuCommand.CopyFullPath).IsEnabled);
		Assert.True(Find(entries, ProjectTreeContextMenuCommand.CopyRelativePath).IsEnabled);
	}

	[Fact]
	public void RelativePath_IncludesRootFolderAndUsesForwardSlashes()
	{
		using var temporary = new TemporaryDirectory();
		var root = temporary.CreateFolder("проект");
		var nested = Path.Combine(root, "src", "данные", "file.cs");

		Assert.Equal("проект", ProjectTreePathUtility.GetRelativeDisplayPath(root, root));
		Assert.Equal(
			"проект/src/данные/file.cs",
			ProjectTreePathUtility.GetRelativeDisplayPath(root, nested));
	}

	[Theory]
	[InlineData(false, "OpenInFileManager,-,CopyFullPath,CopyRelativePath,CopyContent")]
	[InlineData(true, "OpenInFileManager,-,CopyFullPath,CopyRelativePath,-,ExpandBranch")]
	public void Build_WithoutAnotherSelection_OmitsSelectOnly(
		bool isDirectory,
		string expected)
	{
		var entries = ProjectTreeContextMenuPolicy.Build(
			isDirectory,
			isExpanded: false,
			allowContentAndSelection: true,
			showSelectOnly: false);

		Assert.Equal(expected.Split(','), Describe(entries));
	}

	private static string[] Describe(IReadOnlyList<ProjectTreeContextMenuEntry> entries) =>
		entries.Select(static entry =>
			entry.Kind == ProjectTreeContextMenuEntryKind.Separator
				? "-"
				: entry.Command!.Value.ToString()).ToArray();

	private static ProjectTreeContextMenuEntry Find(
		IEnumerable<ProjectTreeContextMenuEntry> entries,
		ProjectTreeContextMenuCommand command) =>
		Assert.Single(entries, entry => entry.Command == command);
}
