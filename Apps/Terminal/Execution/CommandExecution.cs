using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.Rendering;
using DevProjex.Application.Secrets;

namespace DevProjex.Terminal.Execution;

internal static class CommandExecution
{
	public static async Task<int> RunAsync(
		ITerminalEnvironment environment,
		TerminalOutputOptions outputOptions,
		Func<Task<int>> operation,
		LocalizationService? localization = null)
	{
		var text = localization ?? new LocalizationService(
			new JsonLocalizationCatalog(),
			AppLanguage.En);
		try
		{
			return await operation().ConfigureAwait(false);
		}
		catch (TerminalBrokenPipeException)
		{
			return CommandLineExitCodes.Success;
		}
		catch (OperationCanceledException)
		{
			new ErrorRenderer(environment, outputOptions, text).Write(new TerminalError(
				"DPX-CLI-CANCELED",
				text["Terminal.Error.Canceled"],
				ExitCode: CommandLineExitCodes.Canceled));
			return CommandLineExitCodes.Canceled;
		}
		catch (PortableProjectProfileException exception)
		{
			var isDestinationConflict = exception.Code == "DPX-PROFILE-DESTINATION-EXISTS";
			var isRuntimeFailure = exception.Code == "DPX-CLI-PROFILE-WRITE-FAILED";
			return WriteError(environment, outputOptions, text, new TerminalError(
				exception.Code,
				SafeMessageFor(exception.Code, text),
				isDestinationConflict
					? text["Terminal.Hint.DestinationForce"]
					: null,
				ExitCode: isDestinationConflict
					? CommandLineExitCodes.DestinationConflict
					: isRuntimeFailure
						? CommandLineExitCodes.RuntimeError
						: CommandLineExitCodes.UsageError,
				Exception: exception));
		}
		catch (ProjectContextValidationException exception)
		{
			return WriteError(environment, outputOptions, text, new TerminalError(
				exception.Code,
				SafeMessageFor(exception.Code, text),
				HintFor(exception.Code, text),
				ExitCode: CommandLineExitCodes.UsageError,
				Exception: exception,
				ContextPath: exception.ContextPath));
		}
		catch (DesktopControl.DesktopControlException exception)
		{
			var exitCode = exception.Code == ProjectContextGitReadiness.UnavailableDiagnosticCode
				? CommandLineExitCodes.PolicyFailure
				: exception.ExitCode;
			return WriteError(environment, outputOptions, text, new TerminalError(
				exception.Code,
				SafeMessageFor(exception.Code, text),
				ExitCode: exitCode,
				Exception: exception));
		}
		catch (ProjectCopyExportException exception)
		{
			var error = ProjectCopyTerminalErrorMapper.Map(exception, text);
			return WriteError(environment, outputOptions, text, error);
		}
		catch (TerminalProjectSourceException exception)
		{
			return WriteError(environment, outputOptions, text, new TerminalError(
				exception.Code,
				SafeMessageFor(exception.Code, text),
				ExitCode: exception.ExitCode,
				Exception: exception));
		}
		catch (SecretScanLimitExceededException exception)
		{
			return WriteError(environment, outputOptions, text, new TerminalError(
				"DPX-SECRET-SCAN-LIMIT-EXCEEDED",
				text["Error.ProjectCopy.SecretScanLimitExceeded"],
				ExitCode: CommandLineExitCodes.RuntimeError,
				Exception: exception,
				ContextPath: exception.Path));
		}
		catch (SecretDetectionException exception)
		{
			return WriteError(environment, outputOptions, text, new TerminalError(
				"DPX-SECRET-DETECTION-FAILED",
				text["Error.ProjectCopy.SecretDetectionFailed"],
				ExitCode: CommandLineExitCodes.RuntimeError,
				Exception: exception));
		}
		catch (OutputDestinationConflictException exception)
		{
			return WriteError(environment, outputOptions, text, new TerminalError(
				"DPX-EXPORT-DESTINATION-EXISTS",
				text["Terminal.Error.DestinationExists"],
				text["Terminal.Hint.DestinationForce"],
				CommandLineExitCodes.DestinationConflict,
				exception,
				exception.Path));
		}
		catch (UnauthorizedAccessException exception)
		{
			return WriteError(environment, outputOptions, text, new TerminalError(
				"DPX-IO-ACCESS-DENIED",
				text["Terminal.Error.AccessDenied"],
				ExitCode: CommandLineExitCodes.RuntimeError,
				Exception: exception));
		}
		catch (Exception exception) when (exception is IOException or DirectoryNotFoundException or FileNotFoundException)
		{
			return WriteError(environment, outputOptions, text, new TerminalError(
				"DPX-IO-FAILURE",
				text["Terminal.Error.IoFailure"],
				ExitCode: CommandLineExitCodes.RuntimeError,
				Exception: exception));
		}
		catch (Exception exception)
		{
			return WriteError(environment, outputOptions, text, new TerminalError(
				"DPX-CLI-UNEXPECTED",
				text["Terminal.Error.Unexpected"],
				ExitCode: CommandLineExitCodes.RuntimeError,
				Exception: exception));
		}
	}

	private static int WriteError(
		ITerminalEnvironment environment,
		TerminalOutputOptions outputOptions,
		LocalizationService localization,
		TerminalError error)
	{
		new ErrorRenderer(environment, outputOptions, localization).Write(error);
		return error.ExitCode;
	}

	private static string SafeMessageFor(string code, LocalizationService localization) => code switch
	{
		"DPX-PROJECT-PATH-REQUIRED" => localization["Terminal.Error.ProjectPathRequired"],
		"DPX-PROJECT-NOT-FOUND" => localization["Terminal.Error.ProjectNotFound"],
		"DPX-SELECTION-PATH-INVALID" => localization["Terminal.Error.SelectionPathInvalid"],
		"DPX-SELECTION-PATH-MISSING" => localization["Terminal.Diagnostic.SelectedPathMissing"],
		"DPX-CLI-SELECT-FROM-INVALID" => localization["Terminal.Error.SelectFromInvalid"],
		"DPX-CLI-GIT-URL-INVALID" => localization["Terminal.Error.RepositoryUrlInvalid"],
		"DPX-CLI-GIT-BRANCH-INVALID" => localization["Terminal.Error.BranchInvalid"],
		"DPX-CLI-GIT-BRANCH-LOCAL" => localization["Terminal.Error.BranchLocal"],
		"DPX-CLI-GIT-BRANCH-UNAVAILABLE" => localization["Terminal.Error.BranchUnavailable"],
		"DPX-CLI-GIT-UNAVAILABLE" => localization["Terminal.Error.GitUnavailable"],
		"DPX-CLI-GIT-CLONE-FAILED" => localization["Terminal.Error.CloneFailed"],
		"DPX-CLI-GIT-CACHE-FAILED" => localization["Terminal.Error.CloneCacheFailed"],
		"DPX-CLI-PROFILE-NOT-FOUND" => localization["Terminal.Error.ProfileUnresolved"],
		"DPX-CLI-PROFILE-UNRESOLVED" => localization["Terminal.Error.ProfileUnresolved"],
		"DPX-CLI-PROFILE-INVALID" => localization["Terminal.Error.ProfileInvalid"],
		"DPX-CLI-PROFILE-WRITE-FAILED" => localization["Terminal.Error.ProfileWriteFailed"],
		"DPX-PROFILE-DESTINATION-EXISTS" => localization["Terminal.Error.ProfileDestinationExists"],
		"DPX-CLI-FORCE-NOT-SUPPORTED" => localization["Terminal.Error.ForceNotSupported"],
		"DPX-CLI-ZIP-EXTENSION-REQUIRED" => localization["Terminal.Error.ZipExtensionRequired"],
		"DPX-DESKTOP-AMBIGUOUS" => localization["Terminal.Error.DesktopAmbiguous"],
		"DPX-DESKTOP-NOT-RUNNING" => localization["Terminal.Error.DesktopNotRunning"],
		"DPX-DESKTOP-TIMEOUT" => localization["Terminal.Error.DesktopTimeout"],
		"DPX-DESKTOP-PROTOCOL-MISMATCH" => localization["Terminal.Error.DesktopProtocolMismatch"],
		"DPX-DESKTOP-PAYLOAD-TOO-LARGE" => localization["Terminal.Error.DesktopPayloadTooLarge"],
		ProjectContextGitReadiness.UnavailableDiagnosticCode =>
			localization["Terminal.Diagnostic.TrackedIndexUnavailable"],
		var value when value.StartsWith("DPX-DESKTOP-", StringComparison.Ordinal) =>
			localization["Terminal.Error.DesktopRequestFailed"],
		_ => localization["Terminal.Error.CommandInvalid"]
	};

	private static string? HintFor(string code, LocalizationService localization) => code switch
	{
		"DPX-CLI-PROFILE-NOT-FOUND" => localization["Terminal.Hint.ProfileStandard"],
		_ => null
	};
}
