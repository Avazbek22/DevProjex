using System.Collections.Frozen;

namespace DevProjex.Kernel.Models;

public sealed record SmartIgnoreRuleDescriptor(
	IReadOnlySet<string> MarkerFiles,
	IReadOnlySet<string> MarkerExtensions,
	IReadOnlySet<string> FolderNames,
	IReadOnlySet<string> FileNames,
	IReadOnlySet<string> EvidenceRequiredFolderNames)
{
	private static readonly FrozenSet<string> EmptyNames =
		Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	public SmartIgnoreRuleDescriptor(
		IReadOnlySet<string> markerFiles,
		IReadOnlySet<string> markerExtensions,
		IReadOnlySet<string> folderNames,
		IReadOnlySet<string> fileNames)
		: this(markerFiles, markerExtensions, folderNames, fileNames, EmptyNames)
	{
	}

	public static SmartIgnoreRuleDescriptor Empty { get; } = new(
		EmptyNames,
		EmptyNames,
		EmptyNames,
		EmptyNames,
		EmptyNames);
}
