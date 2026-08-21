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
			allowContentAndSelection: true);

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
			allowContentAndSelection: true);

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
			allowContentAndSelection: false);

		Assert.False(Find(entries, ProjectTreeContextMenuCommand.CopyContent).IsEnabled);
		Assert.False(Find(entries, ProjectTreeContextMenuCommand.SelectOnly).IsEnabled);
		Assert.True(Find(entries, ProjectTreeContextMenuCommand.OpenInFileManager).IsEnabled);
		Assert.True(Find(entries, ProjectTreeContextMenuCommand.CopyFullPath).IsEnabled);
		Assert.True(Find(entries, ProjectTreeContextMenuCommand.CopyRelativePath).IsEnabled);
	}

	[Fact]
	public void RelativePath_RootIsDotAndNestedPathsUseForwardSlashes()
	{
		using var temporary = new TemporaryDirectory();
		var root = temporary.CreateFolder("проект");
		var nested = Path.Combine(root, "src", "данные", "file.cs");

		Assert.Equal(".", ProjectTreePathUtility.GetRelativeDisplayPath(root, root));
		Assert.Equal(
			"src/данные/file.cs",
			ProjectTreePathUtility.GetRelativeDisplayPath(root, nested));
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
