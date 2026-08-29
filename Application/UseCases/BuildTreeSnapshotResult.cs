namespace DevProjex.Application.UseCases;

public sealed record BuildTreeSnapshotResult(
	BuildTreeResult Tree,
	ProjectTreeInventorySnapshot? Inventory,
	IReadOnlyList<ContextDiagnostic>? Diagnostics = null);
