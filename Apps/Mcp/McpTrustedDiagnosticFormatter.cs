using System.Globalization;

namespace DevProjex.Mcp;

internal static class McpTrustedDiagnosticFormatter
{
	private const string MissingSelectedPathCode = "DPX-SELECTION-PATH-MISSING";
	private const string PartialProjectAccessCode = "DPX-PROJECT-PARTIAL-ACCESS";
	private const string ProjectSelectionWarningCode = "DPX-PROJECT-SELECTION-WARNING";

	public static string? FormatWarnings(ProjectContextPlan plan)
	{
		ArgumentNullException.ThrowIfNull(plan);
		return FormatWarnings(plan.Diagnostics);
	}

	/// <summary>
	/// Whether the plan reported a requested path the effective tree does not hold. Tools that
	/// tolerate a missing path answer over the rest of the selection, so this warning is the only
	/// signal that part of the request selected nothing.
	/// </summary>
	public static bool ReportsMissingSelectedPath(ProjectContextPlan plan)
	{
		ArgumentNullException.ThrowIfNull(plan);
		return plan.Diagnostics.Any(static diagnostic =>
			diagnostic.Severity == ContextDiagnosticSeverity.Warning &&
			string.Equals(diagnostic.Code, MissingSelectedPathCode, StringComparison.Ordinal));
	}

	internal static string? FormatWarnings(IReadOnlyList<ContextDiagnostic> diagnostics)
	{
		ArgumentNullException.ThrowIfNull(diagnostics);
		var warnings = diagnostics
			.Where(static diagnostic => diagnostic.Severity == ContextDiagnosticSeverity.Warning)
			.GroupBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal);
		StringBuilder? output = null;
		foreach (var group in warnings)
		{
			var groupedDiagnostics = group.ToArray();
			var safeCode = SafeCode(group.Key);
			var message = FormatSafeMessage(safeCode, groupedDiagnostics);
			output ??= new StringBuilder();
			if (output.Length > 0)
				output.AppendLine();
			output.Append("[Warning ")
				.Append(safeCode)
				.Append("] ")
				.Append(message);
		}

		return output?.ToString();
	}

	internal static string? FormatBlocking(ContextDiagnostic diagnostic)
	{
		ArgumentNullException.ThrowIfNull(diagnostic);
		if (diagnostic.Code != GitScopeFilter.UnsafeFilterDiagnosticCode)
			return null;
		return $"[Error {GitScopeFilter.UnsafeFilterDiagnosticCode}] Exact working-tree comparison was refused because an untrusted Git filter is configured.";
	}

	private static string SafeCode(string code) => code switch
	{
		GitScopeFilter.DeletedDiagnosticCode => GitScopeFilter.DeletedDiagnosticCode,
		GitScopeFilter.UnsafeFilterDiagnosticCode => GitScopeFilter.UnsafeFilterDiagnosticCode,
		ProjectContextGitReadiness.PartialDiagnosticCode => ProjectContextGitReadiness.PartialDiagnosticCode,
		MissingSelectedPathCode => MissingSelectedPathCode,
		PartialProjectAccessCode => PartialProjectAccessCode,
		ProjectSelectionWarningCode => ProjectSelectionWarningCode,
		_ => ProjectSelectionWarningCode
	};

	private static string FormatSafeMessage(
		string code,
		IReadOnlyList<ContextDiagnostic> diagnostics) =>
		code switch
		{
			GitScopeFilter.DeletedDiagnosticCode => FormatDeletedFiles(diagnostics),
			GitScopeFilter.UnsafeFilterDiagnosticCode =>
				"Exact working-tree comparison was refused because an untrusted Git filter is configured.",
			ProjectContextGitReadiness.PartialDiagnosticCode =>
				"Some nested Git indexes could not be read; those repository scopes were excluded. Results are partial.",
			MissingSelectedPathCode => FormatMissingPaths(diagnostics.Count),
			PartialProjectAccessCode =>
				"Some project paths could not be read. Results are partial; check project permissions and retry.",
			ProjectSelectionWarningCode =>
				$"The project scan reported {FormatCount(diagnostics.Count, "selection warning", "selection warnings")}. " +
				"Results may be partial; inspect the project locally or narrow the request and retry.",
			_ =>
				"The project selection reported a warning. Results may be partial; inspect the project locally and retry."
		};

	private static string FormatDeletedFiles(IReadOnlyList<ContextDiagnostic> diagnostics)
	{
		var count = diagnostics.Sum(static diagnostic => Math.Max(0, diagnostic.Count ?? 0));
		return count > 0
			? $"Deleted files excluded from the Git state: {count.ToString(CultureInfo.InvariantCulture)}."
			: "Deleted files from the Git state were excluded because they are absent from the working tree.";
	}

	private static string FormatMissingPaths(int count) =>
		$"{FormatCount(count, "requested path is", "requested paths are")} not present in the effective project tree. " +
		"Call get_tree to refresh available paths, then retry with existing paths.";

	private static string FormatCount(int count, string singular, string plural) =>
		$"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? singular : plural)}";
}
