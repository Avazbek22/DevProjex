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
	TerminalOutputOptions Output,
	bool IncludeFindings = false,
	bool FailOnFindings = false,
	bool Force = false,
	int? TopFiles = null,
	long? MaxFileBytes = null,
	string? RepositorySourceUrl = null);

public sealed record TreeCommandRequest(
	string ProjectPath,
	ProjectSelectionSpec Selection,
	TreeTextFormat Format,
	string? OutputPath,
	TerminalOutputOptions Output,
	bool Force = false,
	long? MaxFileBytes = null,
	string? RepositorySourceUrl = null);

public sealed record ExportContextCommandRequest(
	string ProjectPath,
	ProjectSelectionSpec Selection,
	ProjectContextView View,
	ProjectContextDocumentFormat Format,
	string? OutputPath,
	bool Force,
	bool DryRun,
	long? MaximumEstimatedTokens,
	TerminalOutputOptions Output,
	long? MaxFileBytes = null,
	string? RepositorySourceUrl = null);

public sealed record ExportProjectCommandRequest(
	string ProjectPath,
	ProjectSelectionSpec Selection,
	ProjectCopyExportFormat Format,
	string OutputPath,
	bool Force,
	bool DryRun,
	TerminalOutputOptions Output,
	string? RepositorySourceUrl = null);
