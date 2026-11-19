namespace DevProjex.Terminal.Execution;

/// <summary>
/// Maps error-severity plan diagnostics to the process exit code the error catalog in
/// Docs/CLI-Output-Contract.md assigns to their codes.
/// </summary>
internal static class PlanDiagnosticExitCode
{
	// Plan diagnostic codes the catalog lists with usage exit code 2. Every other error code a plan
	// carries (Git state, tracked index, refused filters) is a policy failure.
	private static readonly HashSet<string> UsageErrorCodes = new(StringComparer.Ordinal)
	{
		"DPX-PROJECT-NOT-FOUND",
		"DPX-PROJECT-PATH-REQUIRED",
		"DPX-PROJECT-PATH-INVALID",
		"DPX-PROJECT-ROOT-ACCESS-DENIED",
		"DPX-SELECTION-PATH-INVALID",
		"DPX-SELECTION-PATH-MISSING"
	};

	public static int For(IEnumerable<ContextDiagnostic> diagnostics)
	{
		ArgumentNullException.ThrowIfNull(diagnostics);
		var errorCodes = diagnostics
			.Where(static diagnostic => diagnostic.Severity == ContextDiagnosticSeverity.Error)
			.Select(static diagnostic => diagnostic.Code)
			.ToArray();
		if (errorCodes.Length == 0)
			return CommandLineExitCodes.Success;

		// Invalid input outranks a policy refusal: once the request itself is wrong, the policy result says nothing.
		return errorCodes.Any(UsageErrorCodes.Contains)
			? CommandLineExitCodes.UsageError
			: CommandLineExitCodes.PolicyFailure;
	}

	public static int ForCode(string code)
	{
		ArgumentNullException.ThrowIfNull(code);
		return UsageErrorCodes.Contains(code)
			? CommandLineExitCodes.UsageError
			: CommandLineExitCodes.PolicyFailure;
	}
}
