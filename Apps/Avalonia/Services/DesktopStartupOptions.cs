using DevProjex.Application.DesktopControl;

namespace DevProjex.Avalonia.Services;

public enum DesktopDiagnosticScenario
{
	Standard,
	PreviewSearchRetention,
	ProjectMemoryLifecycle
}

public sealed record SessionMetricsOptions(
	bool Enabled,
	string? ProjectPath,
	string? OutputPath)
{
	public static SessionMetricsOptions Disabled { get; } = new(false, null, null);
}

public sealed record DesktopStartupOptions(
	DesktopOpenRequest? OpenRequest = null,
	SessionMetricsOptions? SessionMetrics = null,
	DesktopDiagnosticScenario? DiagnosticScenario = null,
	bool ElevationAttempted = false)
{
	public static DesktopStartupOptions Default { get; } = new();

	public SessionMetricsOptions EffectiveSessionMetrics =>
		SessionMetrics ?? SessionMetricsOptions.Disabled;
}
