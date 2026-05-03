namespace DevProjex.Kernel.Models;

public sealed record SmartIgnoreRuleDescriptor(
	IReadOnlySet<string> MarkerFiles,
	IReadOnlySet<string> MarkerExtensions,
	IReadOnlySet<string> FolderNames,
	IReadOnlySet<string> FileNames)
{
	public static SmartIgnoreRuleDescriptor Empty { get; } = new(
		new HashSet<string>(StringComparer.OrdinalIgnoreCase),
		new HashSet<string>(StringComparer.OrdinalIgnoreCase),
		new HashSet<string>(StringComparer.OrdinalIgnoreCase),
		new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}
