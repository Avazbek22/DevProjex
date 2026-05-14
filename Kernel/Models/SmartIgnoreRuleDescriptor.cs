using System.Collections.Frozen;

namespace DevProjex.Kernel.Models;

public sealed record SmartIgnoreRuleDescriptor(
	IReadOnlySet<string> MarkerFiles,
	IReadOnlySet<string> MarkerExtensions,
	IReadOnlySet<string> FolderNames,
	IReadOnlySet<string> FileNames)
{
	private static readonly FrozenSet<string> EmptyNames =
		Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	public static SmartIgnoreRuleDescriptor Empty { get; } = new(
		EmptyNames,
		EmptyNames,
		EmptyNames,
		EmptyNames);
}
