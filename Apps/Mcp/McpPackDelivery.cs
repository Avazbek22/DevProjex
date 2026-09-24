using DevProjex.Application.Secrets;

namespace DevProjex.Mcp;

internal static class McpPackDelivery
{
	public static IReadOnlyList<UnscannableFile> MergeUnscannableFiles(
		IReadOnlyList<UnscannableFile> admissionFiles,
		IReadOnlyList<UnscannableFile> preparedFiles) => admissionFiles
		.Concat(preparedFiles)
		.GroupBy(static file => file.Path, PathComparer.Default)
		.Select(static group => group.Last())
		.ToArray();

	public static IReadOnlyList<string> DeliveredPaths(
		IEnumerable<string> admittedPaths,
		IReadOnlyList<UnscannableFile> unscannableFiles) => admittedPaths
		.Except(unscannableFiles.Select(static file => file.Path), PathComparer.Default)
		.ToArray();
}
