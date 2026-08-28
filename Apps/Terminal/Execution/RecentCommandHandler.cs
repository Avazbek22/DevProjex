using System.Globalization;
using System.Text.Json;
using DevProjex.Application.Workspaces;
using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Rendering;

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

	public int Execute(
		CliRecentKind kind,
		int limit,
		CliTextJsonFormat format,
		TerminalOutputOptions outputOptions)
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

		foreach (var line in FormatTextEntries(entries, services.Localization, environment, outputOptions))
			environment.Output.WriteLine(line);

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

	private static string NormalizePath(string path) => PathUtility.NormalizeSeparators(path);

	internal static IReadOnlyList<string> FormatTextEntries(
		IReadOnlyList<RecentOutputEntry> entries) =>
		TerminalColumnLayout.Format(entries.Select(static entry => new[]
		{
			TerminalTextEscaping.EscapeSingleLine(entry.Kind),
			TerminalTextEscaping.EscapeSingleLine(entry.Name),
			TerminalTextEscaping.EscapeSingleLine(entry.Path ?? entry.Url ?? string.Empty),
			entry.LastOpened.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
		}).ToArray());

	internal static IReadOnlyList<string> FormatTextEntries(
		IReadOnlyList<RecentOutputEntry> entries,
		LocalizationService localization,
		ITerminalEnvironment environment,
		TerminalOutputOptions outputOptions)
	{
		if (!environment.IsOutputInteractive || environment.IsTermDumb)
		{
			return TerminalColumnLayout.Format(entries.Select(entry => new[]
			{
				LocalizeKind(entry.Kind, localization),
				TerminalTextEscaping.EscapeSingleLine(entry.Name),
				TerminalTextEscaping.EscapeSingleLine(entry.Path ?? entry.Url ?? string.Empty),
				entry.LastOpened.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
			}).ToArray());
		}

		var rows = entries.Select((entry, index) => new[]
		{
			(index + 1).ToString(CultureInfo.InvariantCulture),
			LocalizeKind(entry.Kind, localization),
			TerminalTextEscaping.EscapeSingleLine(entry.Name),
			TerminalTextEscaping.EscapeSingleLine(entry.Path ?? entry.Url ?? string.Empty),
			entry.LastOpened.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
		}).ToArray();
		return TerminalColumnLayout.FormatForOutput(
			rows,
			[
				"#",
				localization["Terminal.Table.Type"],
				localization["Terminal.Tui.Source"],
				localization["Terminal.Tui.Recent.Path"],
				localization["Terminal.Tui.Recent.LastOpened"]
			],
			environment,
			outputOptions,
			truncationColumn: 3);
	}

	private static string LocalizeKind(string kind, LocalizationService localization) =>
		localization[kind == "folder"
			? "Terminal.Tui.Folder"
			: "Terminal.Tui.RecentRepositories.Repository"];

	internal sealed record RecentOutputEntry(
		string Kind,
		string? Path,
		string? Url,
		string Name,
		string? Parent,
		DateTimeOffset LastOpened);
}
