namespace DevProjex.Kernel;

/// <summary>
/// Defines the identity and deterministic ordering of entries already discovered in a project tree.
/// Filesystems can expose names that differ only by case even when the host platform normally uses
/// case-insensitive path lookup, so tree entries must never use the platform path comparer as identity.
/// </summary>
public static class ProjectTreePathIdentity
{
	public static StringComparer CanonicalComparer => StringComparer.Ordinal;

	public static StringComparison CanonicalComparison => StringComparison.Ordinal;
}
