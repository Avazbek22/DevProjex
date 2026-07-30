using DevProjex.Terminal.CommandLine;

namespace DevProjex.Terminal.Execution;

public enum AnalysisOutputFormat
{
	Text,
	Json
}

public sealed record AnalyzeCommandRequest(
	string ProjectPath,
	ProjectSelectionSpec Selection,
	AnalysisOutputFormat Format,
	string? OutputPath,
	bool Strict,
	TerminalOutputOptions Output);

public sealed record ExportContextCommandRequest(
	string ProjectPath,
	ProjectSelectionSpec Selection,
	ProjectContextView View,
	ProjectContextDocumentFormat Format,
	string? OutputPath,
	bool Force,
	bool DryRun,
	TerminalOutputOptions Output);

public sealed record ExportProjectCommandRequest(
	string ProjectPath,
	ProjectSelectionSpec Selection,
	ProjectCopyExportFormat Format,
	string OutputPath,
	bool Force,
	bool DryRun,
	TerminalOutputOptions Output);
