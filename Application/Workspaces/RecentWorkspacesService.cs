namespace DevProjex.Application.Workspaces;

public enum RecentWorkspaceKind
{
	Folder,
	Repository
}

public sealed record RecentWorkspaceSource(
	RecentWorkspaceKind Kind,
	string Source,
	DateTimeOffset OpenedUtc);

public sealed record RecentWorkspaceDescriptor(
	RecentWorkspaceKind Kind,
	string Source,
	string DisplaySource,
	string DisplayName,
	string IdentityKey,
	DateTimeOffset OpenedUtc);

/// <summary>
/// Projects persisted folder and repository history into one user-facing workspace model.
/// Persistence and cache inspection remain separate so initial projection performs no I/O.
/// </summary>
public sealed class RecentWorkspacesService
{
	public IReadOnlyList<RecentWorkspaceDescriptor> Project(
		IEnumerable<RecentWorkspaceSource> sources)
	{
		ArgumentNullException.ThrowIfNull(sources);
		return sources
			.Where(static source => !string.IsNullOrWhiteSpace(source.Source))
			.Select(TryCreateDescriptor)
			.OfType<RecentWorkspaceDescriptor>()
			.GroupBy(static workspace => workspace.IdentityKey, StringComparer.Ordinal)
			.Select(static group => group.MaxBy(static workspace => workspace.OpenedUtc)!)
			.OrderByDescending(static workspace => workspace.OpenedUtc)
			.ThenBy(static workspace => workspace.DisplayName, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private static RecentWorkspaceDescriptor? TryCreateDescriptor(RecentWorkspaceSource source)
	{
		if (source.Kind == RecentWorkspaceKind.Repository)
		{
			var displaySource = RepositoryUrlUtility.ToSafeDisplay(source.Source);
			var comparisonKey = RepositoryUrlUtility.GetComparisonKey(displaySource);
			if (comparisonKey.Length == 0)
				return null;

			return new RecentWorkspaceDescriptor(
				source.Kind,
				source.Source,
				displaySource,
				RepositoryUrlUtility.GetRepositoryName(displaySource),
				$"git:{comparisonKey.ToUpperInvariant()}",
				source.OpenedUtc);
		}

		try
		{
			var normalizedPath = PathUtility.Normalize(source.Source);
			var displayName = Path.GetFileName(Path.TrimEndingDirectorySeparator(normalizedPath));
			var identityPath = OperatingSystem.IsWindows()
				? normalizedPath.ToUpperInvariant()
				: normalizedPath;
			return new RecentWorkspaceDescriptor(
				source.Kind,
				normalizedPath,
				normalizedPath,
				string.IsNullOrWhiteSpace(displayName) ? normalizedPath : displayName,
				$"folder:{identityPath}",
				source.OpenedUtc);
		}
		catch
		{
			return null;
		}
	}
}
