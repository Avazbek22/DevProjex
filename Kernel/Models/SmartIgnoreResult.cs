using System.Collections.Frozen;

namespace DevProjex.Kernel.Models;

public sealed record SmartIgnoreResult(
	IReadOnlySet<string> FolderNames,
	IReadOnlySet<string> FileNames)
{
	private static readonly FrozenSet<string> EmptyNames =
		Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	public static SmartIgnoreResult Empty { get; } = new(EmptyNames, EmptyNames);
}
