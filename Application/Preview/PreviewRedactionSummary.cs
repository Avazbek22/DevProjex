namespace DevProjex.Application.Preview;

/// <summary>
/// Describes the redaction notice embedded at the start of a preview document.
/// Clipboard projections use this metadata to keep a redacted fragment self-explanatory.
/// </summary>
public sealed record PreviewRedactionSummary(
	int RedactedCount,
	int LegendLineCount);
