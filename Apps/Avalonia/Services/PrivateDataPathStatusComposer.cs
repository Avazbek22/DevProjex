namespace DevProjex.Avalonia.Services;

internal static class PrivateDataPathStatusComposer
{
	public static PrivateDataPathStatus Compose(
		string? projectRoot,
		ExportPathPresentation? pathPresentation,
		ContentTransformationContext? transformationContext,
		int? contentDetectedCount,
		int? contentHiddenCount)
	{
		var pathResult = OutputRootPathPresentation.ResolveWithRedaction(
			projectRoot ?? string.Empty,
			pathPresentation,
			OutputRootPathPresentation.CaptureRedactionDecision(transformationContext));
		if (!pathResult.HasRedaction)
		{
			return new PrivateDataPathStatus(
				contentDetectedCount,
				contentHiddenCount,
				PathUserNameHidden: null);
		}

		var pathUserNameHidden = pathResult.State == SecretPreviewSpanState.Redacted;
		return new PrivateDataPathStatus(
			(contentDetectedCount ?? 0) + 1,
			(contentHiddenCount ?? 0) + (pathUserNameHidden ? 1 : 0),
			pathUserNameHidden);
	}
}

internal readonly record struct PrivateDataPathStatus(
	int? DetectedCount,
	int? HiddenCount,
	bool? PathUserNameHidden);
