namespace DevProjex.Infrastructure.SmartIgnore;

/// <summary>
/// Smart ignore rule for common system files that clutter project trees.
/// IDE folders (.vs, .idea, .vscode) and VCS folders (.git, .svn, .hg) are now
/// controlled via DotFolders filter for predictable behavior.
/// </summary>
public sealed class CommonSmartIgnoreRule : ISmartIgnoreRule
{
	// System-generated files that should always be filtered
	private static readonly string[] FileNames =
	{
		".ds_store",
		"thumbs.db",
		"desktop.ini"
	};

	public SmartIgnoreResult Evaluate(string rootPath)
	{
		// No folders in CommonSmartIgnore - all folders (.git, .vs, .idea, etc.)
		// are now controlled via DotFolders filter for predictable user control
		var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var files = new HashSet<string>(FileNames, StringComparer.OrdinalIgnoreCase);
		return new SmartIgnoreResult(folders, files);
	}
}
