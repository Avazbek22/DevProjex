using DevProjex.Infrastructure.ProjectProfiles;
using DevProjex.Terminal.DesktopControl;

namespace DevProjex.Tests.Terminal;

public sealed class CommandExecutionTests
{
	[Theory]
	[InlineData("DPX-CLI-PROFILE-INVALID", "profile")]
	[InlineData("DPX-PROJECT-NOT-FOUND", "project")]
	[InlineData("DPX-DESKTOP-NOT-RUNNING", "Desktop")]
	public async Task TypedFailuresNeverExposeRawExceptionMessage(
		string code,
		string expectedSafeText)
	{
		const string secretTechnicalMessage = "RAW_TECHNICAL_SENTINEL";
		var environment = new TestTerminalEnvironment();

		var exitCode = await CommandExecution.RunAsync(
			environment,
			new TerminalOutputOptions(),
			() => throw CreateException(code, secretTechnicalMessage));

		Assert.NotEqual(CommandLineExitCodes.Success, exitCode);
		Assert.Contains(code, environment.StandardError, StringComparison.Ordinal);
		Assert.Contains(expectedSafeText, environment.StandardError, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(secretTechnicalMessage, environment.StandardError, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("DPX-CLI-PROFILE-INVALID", CommandLineExitCodes.UsageError)]
	[InlineData("DPX-CLI-PROFILE-WRITE-FAILED", CommandLineExitCodes.RuntimeError)]
	[InlineData("DPX-PROFILE-DESTINATION-EXISTS", CommandLineExitCodes.DestinationConflict)]
	public async Task PortableProfileFailuresUseContractExitCode(
		string code,
		int expectedExitCode)
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await CommandExecution.RunAsync(
			environment,
			new TerminalOutputOptions(),
			() => throw new PortableProjectProfileException(code, "technical detail"));

		Assert.Equal(expectedExitCode, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains(code, environment.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain("technical detail", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task DesktopTrackedIndexFailureUsesPolicyExitAndLocalizedSafeMessage()
	{
		const string rawMessage = "RAW_TRACKED_INDEX_TECHNICAL_DETAIL";
		var environment = new TestTerminalEnvironment();

		var exitCode = await CommandExecution.RunAsync(
			environment,
			new TerminalOutputOptions(),
			() => throw new DesktopControlException(
				ProjectContextGitReadiness.UnavailableDiagnosticCode,
				rawMessage,
				CommandLineExitCodes.DesktopUnavailable));

		Assert.Equal(CommandLineExitCodes.PolicyFailure, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains(
			ProjectContextGitReadiness.UnavailableDiagnosticCode,
			environment.StandardError,
			StringComparison.Ordinal);
		Assert.Contains("Tracked Git mode", environment.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain(rawMessage, environment.StandardError, StringComparison.Ordinal);
	}

	public static TheoryData<ProjectCopyExportError, string> ProjectCopyErrors => new()
	{
		{ ProjectCopyExportError.InvalidRequest, "DPX-CLI-INVALID-REQUEST" },
		{ ProjectCopyExportError.DestinationInsideSource, "DPX-EXPORT-UNSAFE-DESTINATION" },
		{ ProjectCopyExportError.UnsafeSourcePath, "DPX-EXPORT-UNSAFE-SOURCE" },
		{ ProjectCopyExportError.SymbolicLinkNotSupported, "DPX-EXPORT-SYMLINK-NOT-SUPPORTED" },
		{ ProjectCopyExportError.DestinationUnavailable, "DPX-EXPORT-DESTINATION-UNAVAILABLE" },
		{ ProjectCopyExportError.SourceUnavailable, "DPX-EXPORT-SOURCE-UNAVAILABLE" },
		{ ProjectCopyExportError.AccessDenied, "DPX-IO-ACCESS-DENIED" },
		{ ProjectCopyExportError.IoFailure, "DPX-IO-FAILURE" },
		{ ProjectCopyExportError.UnsafeDestinationPath, "DPX-EXPORT-UNSAFE-DESTINATION" },
		{ ProjectCopyExportError.UnexpectedFailure, "DPX-EXPORT-FAILED" },
		{ ProjectCopyExportError.DestinationConflict, "DPX-EXPORT-DESTINATION-EXISTS" },
		{ ProjectCopyExportError.ReservedNoticeNameConflict, "DPX-EXPORT-RESERVED-NAME" }
	};

	[Theory]
	[MemberData(nameof(ProjectCopyErrors))]
	public async Task EveryProjectCopyFailureHasStableSafeTerminalPresentation(
		ProjectCopyExportError error,
		string expectedCode)
	{
		const string rawMessage = "RAW_PROJECT_COPY_EXCEPTION_MESSAGE";
		var environment = new TestTerminalEnvironment();

		var exitCode = await CommandExecution.RunAsync(
			environment,
			new TerminalOutputOptions(),
			() => throw new ProjectCopyExportException(error, rawMessage));

		Assert.NotEqual(CommandLineExitCodes.Success, exitCode);
		Assert.Contains(expectedCode, environment.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain(rawMessage, environment.StandardError, StringComparison.Ordinal);
	}

	private static Exception CreateException(string code, string message) => code switch
	{
		"DPX-CLI-PROFILE-INVALID" => new PortableProjectProfileException(code, message),
		"DPX-PROJECT-NOT-FOUND" => new ProjectContextValidationException(code, message),
		_ => new DesktopControlException(code, message, CommandLineExitCodes.DesktopUnavailable)
	};
}
