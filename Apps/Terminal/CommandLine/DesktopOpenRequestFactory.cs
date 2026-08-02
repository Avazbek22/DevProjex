namespace DevProjex.Terminal.CommandLine;

internal static class DesktopOpenRequestFactory
{
	public static DesktopOpenRequest Create(
		string? projectPath,
		bool useLastProject,
		bool newWindow,
		bool waitForCompletion,
		bool explicitPreview,
		DesktopPreviewView? previewView,
		TreeTextFormat? treeFormat,
		string? filter,
		string? search,
		ProjectSelectionSpec? selection,
		AppLanguage language,
		bool elevationAttempted) =>
		new(
			projectPath,
			useLastProject,
			newWindow,
			waitForCompletion,
			OpenPreview: explicitPreview ||
			             previewView is not null ||
			             treeFormat is not null ||
			             search is not null,
			PreviewView: previewView ?? DesktopPreviewView.TreeContent,
			TreeFormat: treeFormat,
			Filter: filter,
			Search: search,
			Selection: selection,
			Language: language,
			ElevationAttempted: elevationAttempted);
}
