using System.Text.Json;
using DevProjex.Application.Workspaces;
using DevProjex.Terminal.CommandLine;

namespace DevProjex.Terminal.Execution;

internal sealed class RecentCommandHandler(
	TerminalServices services,
	ITerminalEnvironment environment)
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true
	};

	public int Execute(CliRecentKind kind, int limit, CliTextJsonFormat format)
	{
		var database = services.RecentProjectsStore.Load();
		var sources = database.RecentFolders
			.Select(static entry => new RecentWorkspaceSource(
				RecentWorkspaceKind.Folder,
				entry.Path,
				entry.OpenedUtc))
			.Concat(database.RecentRepositories.Select(static entry => new RecentWorkspaceSource(
				RecentWorkspaceKind.Repository,
				entry.Url,
				entry.OpenedUtc)));
		var entries = services.RecentWorkspacesService
			.Project(sources)
			.Where(entry => Includes(kind, entry.Kind))
			.Take(limit)
			.Select(CreateOutputEntry)
			.ToArray();

		if (format == CliTextJsonFormat.Json)
		{
			environment.Output.WriteLine(JsonSerializer.Serialize(
				new
				{
					schemaVersion = 1,
					kind = "devprojex-recent",
					items = entries
				},
				JsonOptions));
			return CommandLineExitCodes.Success;
		}

		foreach (var entry in entries)
		{
			environment.Output.WriteLine(string.Join(
				'\t',
				entry.Kind,
				entry.Name,
				entry.Path ?? entry.Url,
				entry.LastOpened.ToString("O")));
		}

		return CommandLineExitCodes.Success;
	}

	private static bool Includes(CliRecentKind filter, RecentWorkspaceKind kind) =>
		filter switch
		{
			CliRecentKind.All => true,
			CliRecentKind.Folder => kind == RecentWorkspaceKind.Folder,
			CliRecentKind.Repository => kind == RecentWorkspaceKind.Repository,
			_ => throw new ArgumentOutOfRangeException(nameof(filter), filter, null)
		};

	private static RecentOutputEntry CreateOutputEntry(RecentWorkspaceDescriptor entry)
	{
		var isFolder = entry.Kind == RecentWorkspaceKind.Folder;
		return new RecentOutputEntry(
			isFolder ? "folder" : "repository",
			isFolder ? NormalizePath(entry.Source) : null,
			isFolder ? null : entry.DisplaySource,
			entry.DisplayName,
			isFolder ? ResolveFolderParent(entry.Source) : ResolveRepositoryParent(entry.DisplaySource),
			entry.OpenedUtc.ToUniversalTime());
	}

	private static string? ResolveFolderParent(string path)
	{
		try
		{
			var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
			return Path.GetDirectoryName(normalized) is { Length: > 0 } parent
				? NormalizePath(parent)
				: null;
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
		{
			return null;
		}
	}

	private static string? ResolveRepositoryParent(string url)
	{
		var normalized = url.TrimEnd('/');
		if (normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
			normalized = normalized[..^4];
		var separator = normalized.LastIndexOf('/');
		if (separator < 0)
			separator = normalized.LastIndexOf(':');
		return separator > 0 ? normalized[..separator] : null;
	}

	private static string NormalizePath(string path) => path.Replace('\\', '/');

	private sealed record RecentOutputEntry(
		string Kind,
		string? Path,
		string? Url,
		string Name,
		string? Parent,
		DateTimeOffset LastOpened);
}
