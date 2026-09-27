namespace DevProjex.Terminal.Tui;

public enum TerminalExportKind
{
	Context = 0,
	Folder = 1,
	Zip = 2
}

public enum TerminalExportDestinationState
{
	Ready = 0,
	// The destination exists and Overwrite may replace it.
	Conflict = 1,
	// The destination exists and cannot be replaced: a folder export target, or a
	// directory where a file is expected.
	Blocked = 2
}

public sealed record TerminalExportSummary(
	TerminalExportKind Kind,
	ProjectContextView? View,
	ProjectContextDocumentFormat? DocumentFormat,
	string Destination,
	TerminalExportDestinationState DestinationState,
	int FileCount,
	int FolderCount,
	long Bytes,
	long Characters,
	long EstimatedTokens,
	GitFilteringMode GitMode,
	IReadOnlyList<ProjectExclusion> Exclusions,
	int DiagnosticCount,
	bool SecretsRedacted = false,
	bool PrivateDataRedacted = false,
	string? GitDiffRange = null);

internal readonly record struct TerminalExportCompletion(
	string DestinationPath,
	int SkippedUnscannableCount = 0);

internal enum TerminalExportDecision
{
	Cancel = 0,
	Export = 1,
	DryRun = 2,
	Overwrite = 3
}

internal sealed class TerminalExportDestinationHistory
{
	private static readonly string[] ContextExtensions = [".md", ".txt", ".json", ".xml"];

	private readonly Dictionary<string, Dictionary<TerminalExportKind, string>> _destinations =
		new(PathComparer.Default);

	public string Resolve(string sourceRoot, TerminalExportKind kind, string fallback) =>
		_destinations.GetValueOrDefault(sourceRoot)?.GetValueOrDefault(kind) ?? fallback;

	// A remembered context destination follows the selected format, so a JSON export is
	// never proposed under the name of an earlier text export.
	public string ResolveContext(string sourceRoot, string extension, string fallback)
	{
		var destination = Resolve(sourceRoot, TerminalExportKind.Context, fallback);
		return ContextExtensions.Contains(Path.GetExtension(destination), StringComparer.OrdinalIgnoreCase)
			? Path.ChangeExtension(destination, extension)
			: destination;
	}

	public void Remember(string sourceRoot, TerminalExportKind kind, string destination)
	{
		if (string.IsNullOrWhiteSpace(destination))
			return;
		if (!_destinations.TryGetValue(sourceRoot, out var destinations))
		{
			destinations = [];
			_destinations[sourceRoot] = destinations;
		}
		destinations[kind] = destination;
	}
}
