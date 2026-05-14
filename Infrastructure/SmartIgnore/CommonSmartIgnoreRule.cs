namespace DevProjex.Infrastructure.SmartIgnore;

/// <summary>
/// Smart ignore rule for common system files that clutter project trees.
/// IDE folders (.vs, .idea, .vscode) and VCS folders (.git, .svn, .hg) are now
/// controlled via DotFolders filter for predictable behavior.
/// </summary>
public sealed class CommonSmartIgnoreRule : ISmartIgnoreRule, ISmartIgnoreRuleDescriptorProvider
{
	// System-generated files that should always be filtered
	private static readonly IReadOnlySet<string> FileNames = SmartIgnoreRuleSet.Create(
		".ds_store",
		"thumbs.db",
		"desktop.ini");

	private static readonly SmartIgnoreResult RuleResult =
		SmartIgnoreRuleSet.Result(fileNames: FileNames);

	public SmartIgnoreRuleDescriptor Descriptor { get; } =
		SmartIgnoreRuleSet.Descriptor(fileNames: FileNames);

	public SmartIgnoreResult Evaluate(string rootPath)
	{
		// No folders in CommonSmartIgnore - all folders (.git, .vs, .idea, etc.)
		// are now controlled via DotFolders filter for predictable user control
		return RuleResult;
	}
}
