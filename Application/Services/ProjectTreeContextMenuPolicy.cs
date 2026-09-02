namespace DevProjex.Application.Services;

public enum ProjectTreeContextMenuCommand
{
	OpenInFileManager,
	CopyFullPath,
	CopyRelativePath,
	CopyContent,
	SelectOnly,
	ExpandBranch,
	CollapseBranch
}

public enum ProjectTreeContextMenuEntryKind
{
	Command,
	Separator
}

public readonly record struct ProjectTreeContextMenuEntry(
	ProjectTreeContextMenuEntryKind Kind,
	ProjectTreeContextMenuCommand? Command = null,
	bool IsEnabled = true);

public static class ProjectTreeContextMenuPolicy
{
	public static IReadOnlyList<ProjectTreeContextMenuEntry> Build(
		bool isDirectory,
		bool isExpanded,
		bool allowContentAndSelection,
		bool showSelectOnly)
	{
		var entries = new List<ProjectTreeContextMenuEntry>(isDirectory ? 8 : 7)
		{
			Command(ProjectTreeContextMenuCommand.OpenInFileManager),
			Separator(),
			Command(ProjectTreeContextMenuCommand.CopyFullPath),
			Command(ProjectTreeContextMenuCommand.CopyRelativePath)
		};

		if (!isDirectory)
			entries.Add(Command(ProjectTreeContextMenuCommand.CopyContent, allowContentAndSelection));

		if (showSelectOnly || isDirectory)
			entries.Add(Separator());
		if (showSelectOnly)
			entries.Add(Command(ProjectTreeContextMenuCommand.SelectOnly, allowContentAndSelection));
		if (isDirectory)
		{
			entries.Add(Command(
				isExpanded
					? ProjectTreeContextMenuCommand.CollapseBranch
					: ProjectTreeContextMenuCommand.ExpandBranch));
		}

		return entries;
	}

	private static ProjectTreeContextMenuEntry Command(
		ProjectTreeContextMenuCommand command,
		bool isEnabled = true) =>
		new(ProjectTreeContextMenuEntryKind.Command, command, isEnabled);

	private static ProjectTreeContextMenuEntry Separator() =>
		new(ProjectTreeContextMenuEntryKind.Separator);
}
