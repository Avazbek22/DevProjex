using System.Collections.Frozen;

namespace DevProjex.Kernel.Models;

public sealed record SmartIgnoreResult(
	IReadOnlySet<string> FolderNames,
	IReadOnlySet<string> FileNames)
{
	private static readonly FrozenSet<string> EmptyNames =
		Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	public static SmartIgnoreResult Empty { get; } = new(EmptyNames, EmptyNames);

	// Collision-prone names remain visible until their own bounded signature proves
	// generated output. Keeping this metadata with each evaluated scope prevents a
	// merged polyglot workspace from losing the policy of the contributing stack rule.
	public IReadOnlySet<string> EvidenceRequiredFolderNames { get; init; } = EmptyNames;
}
