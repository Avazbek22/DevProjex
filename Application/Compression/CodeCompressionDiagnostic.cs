namespace DevProjex.Application.Compression;

public static class CodeCompressionDiagnostic
{
	public static ContextDiagnostic? Create(CodeCompressionAvailabilitySnapshot availability)
	{
		ArgumentNullException.ThrowIfNull(availability);
		if (!availability.IsUnavailable || availability.PrimaryReason is not { Length: > 0 } reason)
			return null;

		return new ContextDiagnostic(
			CodeCompressionAvailabilitySnapshot.DiagnosticCode,
			ContextDiagnosticSeverity.Warning,
			reason);
	}

	public static ProjectContextPlan Append(
		ProjectContextPlan plan,
		CodeCompressionAvailabilitySnapshot availability)
	{
		ArgumentNullException.ThrowIfNull(plan);
		var diagnostic = Create(availability);
		if (diagnostic is null || plan.Diagnostics.Any(existing =>
		    existing.Code.Equals(diagnostic.Code, StringComparison.Ordinal)))
		{
			return plan;
		}

		return plan with { Diagnostics = [.. plan.Diagnostics, diagnostic] };
	}
}
