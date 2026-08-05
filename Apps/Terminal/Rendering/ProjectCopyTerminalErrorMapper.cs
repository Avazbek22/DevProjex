namespace DevProjex.Terminal.Rendering;

internal static class ProjectCopyTerminalErrorMapper
{
	public static TerminalError Map(
		ProjectCopyExportException exception,
		LocalizationService localization) =>
		exception.Error switch
		{
			ProjectCopyExportError.DestinationConflict => new TerminalError(
				"DPX-EXPORT-DESTINATION-EXISTS",
				localization["Terminal.Error.DestinationExists"],
				localization["Terminal.Hint.DestinationForceZip"],
				CommandLineExitCodes.DestinationConflict,
				exception,
				exception.PathContext),
			ProjectCopyExportError.DestinationInsideSource or
				ProjectCopyExportError.UnsafeDestinationPath => new TerminalError(
					"DPX-EXPORT-UNSAFE-DESTINATION",
					localization["Terminal.Error.UnsafeDestination"],
					ExitCode: CommandLineExitCodes.PolicyFailure,
					Exception: exception),
			ProjectCopyExportError.UnsafeSourcePath => new TerminalError(
				"DPX-EXPORT-UNSAFE-SOURCE",
				localization["Error.ProjectCopy.UnsafeSourcePath"],
				ExitCode: CommandLineExitCodes.RuntimeError,
				Exception: exception),
			ProjectCopyExportError.SymbolicLinkNotSupported => new TerminalError(
				"DPX-EXPORT-SYMLINK-NOT-SUPPORTED",
				localization["Error.ProjectCopy.SymbolicLinkNotSupported"],
				ExitCode: CommandLineExitCodes.RuntimeError,
				Exception: exception),
			ProjectCopyExportError.DestinationUnavailable => new TerminalError(
				"DPX-EXPORT-DESTINATION-UNAVAILABLE",
				localization["Error.ProjectCopy.DestinationUnavailable"],
				ExitCode: CommandLineExitCodes.RuntimeError,
				Exception: exception),
			ProjectCopyExportError.AccessDenied => new TerminalError(
				"DPX-IO-ACCESS-DENIED",
				localization["Terminal.Error.AccessDenied"],
				ExitCode: CommandLineExitCodes.RuntimeError,
				Exception: exception),
			ProjectCopyExportError.SourceUnavailable => new TerminalError(
				"DPX-EXPORT-SOURCE-UNAVAILABLE",
				localization["Terminal.Error.SourceUnavailable"],
				ExitCode: CommandLineExitCodes.RuntimeError,
				Exception: exception),
			ProjectCopyExportError.InvalidRequest => new TerminalError(
				"DPX-CLI-INVALID-REQUEST",
				localization["Terminal.Error.InvalidRequest"],
				ExitCode: CommandLineExitCodes.UsageError,
				Exception: exception),
			ProjectCopyExportError.IoFailure => new TerminalError(
				"DPX-IO-FAILURE",
				localization["Terminal.Error.IoFailure"],
				ExitCode: CommandLineExitCodes.RuntimeError,
				Exception: exception),
			ProjectCopyExportError.SecretDetectionFailed => new TerminalError(
				"DPX-SECRET-DETECTION-FAILED",
				localization["Error.ProjectCopy.SecretDetectionFailed"],
				ExitCode: CommandLineExitCodes.RuntimeError,
				Exception: exception,
				ContextPath: exception.PathContext),
			ProjectCopyExportError.SecretScanLimitExceeded => new TerminalError(
				"DPX-SECRET-SCAN-LIMIT-EXCEEDED",
				localization["Error.ProjectCopy.SecretScanLimitExceeded"],
				ExitCode: CommandLineExitCodes.RuntimeError,
				Exception: exception,
				ContextPath: exception.PathContext),
			ProjectCopyExportError.UnexpectedFailure => new TerminalError(
				"DPX-EXPORT-FAILED",
				localization["Terminal.Error.ExportFailed"],
				ExitCode: CommandLineExitCodes.RuntimeError,
				Exception: exception),
			_ => new TerminalError(
				"DPX-EXPORT-FAILED",
				localization["Terminal.Error.ExportFailed"],
				ExitCode: CommandLineExitCodes.RuntimeError,
				Exception: exception)
		};
}
