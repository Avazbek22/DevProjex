namespace DevProjex.Mcp;

/// <summary>
/// The selection baseline a server runs with when its startup line names none.
/// </summary>
public static class McpServerBaseline
{
	/// <summary>
	/// The startup token that expands to <see cref="DefaultExclusions"/>, so a startup line can
	/// extend the default set ("--exclude default --exclude dot-folders") instead of re-listing it.
	/// </summary>
	public const string DefaultExclusionsToken = "default";

	// The desktop standard set was designed for a person who sees the checkboxes and the
	// hidden-entry counts next to them. An agent sees neither, so a toggle that hides a
	// deliberate repository file — Dockerfile, LICENSE, .github/, .env.example, an empty
	// __init__.py — turns into a confident wrong answer about the project. The server default
	// therefore keeps only the toggles that remove noise no agent wants: dependency and build
	// trees, and folders the other filters leave empty. Every other toggle is opt-in on the
	// startup line, where the person configuring the server can see the choice.
	public static IReadOnlyCollection<ProjectExclusion> DefaultExclusions { get; } =
	[
		ProjectExclusion.SmartIgnore,
		ProjectExclusion.EmptyFolders
	];

	/// <summary>The Git baseline used when the startup line names none: the standard profile's mode.</summary>
	public static GitFilteringMode DefaultGitMode { get; } =
		ProjectSelectionSpec.Standard.GitMode ?? GitFilteringMode.RespectGitIgnore;
}
