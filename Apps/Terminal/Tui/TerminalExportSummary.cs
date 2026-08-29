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
	Conflict = 1
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
	bool RedactionEnabled = false,
	string? GitDiffRange = null);

internal enum TerminalExportDecision
{
	Cancel = 0,
	Export = 1,
	DryRun = 2,
	Overwrite = 3
}

internal sealed class TerminalExportDestinationHistory
{
	private readonly Dictionary<TerminalExportKind, string> _destinations = [];

	public string Resolve(TerminalExportKind kind, string fallback) =>
		_destinations.GetValueOrDefault(kind) ?? fallback;

	public void Remember(TerminalExportKind kind, string destination)
	{
		if (!string.IsNullOrWhiteSpace(destination))
			_destinations[kind] = destination;
	}
}
