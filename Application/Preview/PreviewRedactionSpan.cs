using DevProjex.Application.Secrets;

namespace DevProjex.Application.Preview;

/// <summary>
/// A renderable segment of one detected value. Multi-line values are represented by
/// multiple segments with the same occurrence id so a click on any segment toggles one decision.
/// </summary>
public sealed record PreviewRedactionSpan(
	string OccurrenceId,
	string RuleId,
	int LineNumber,
	int StartColumn,
	int Length,
	SecretPreviewSpanState State,
	int SourceLength = 0,
	SecretFindingSource Source = SecretFindingSource.Detector,
	string? PersistentMarkHash = null,
	string? SessionMarkId = null);
