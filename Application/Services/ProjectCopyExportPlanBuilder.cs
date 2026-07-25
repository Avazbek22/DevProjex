using DevProjex.Application.Selection;

namespace DevProjex.Application.Services;

public sealed class ProjectCopyExportPlanBuilder
{
	public ProjectCopyExportPlan Build(ProjectCopyExportRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentNullException.ThrowIfNull(request.TreeRoot);
		ArgumentNullException.ThrowIfNull(request.SelectedPaths);

		if (string.IsNullOrWhiteSpace(request.ProjectRootPath))
			throw InvalidRequest("The project root path is required.");

		var rootPath = PathUtility.Normalize(request.ProjectRootPath);
		var projectName = NormalizeProjectName(request.ProjectName, rootPath);
		var nodes = ProjectTreeSelectionProjection.BuildIncludedNodes(request.TreeRoot, request.SelectedPaths);
		if (nodes.Count == 0)
			throw InvalidRequest("The selected paths do not belong to the effective project tree.");

		var entries = new List<ProjectCopyExportPlanEntry>(nodes.Count);
		var relativePaths = new HashSet<string>(PathComparer.Default);
		foreach (var node in nodes)
		{
			var sourcePath = PathUtility.Normalize(node.FullPath);
			if (!PathUtility.IsPathInside(sourcePath, rootPath))
			{
				throw new ProjectCopyExportException(
					ProjectCopyExportError.UnsafeSourcePath,
					$"The effective tree contains a path outside the project root: {sourcePath}");
			}

			var relativePath = Path.GetRelativePath(rootPath, sourcePath);
			if (relativePath == ".")
				relativePath = string.Empty;

			if (Path.IsPathRooted(relativePath) || ContainsParentTraversal(relativePath))
			{
				throw new ProjectCopyExportException(
					ProjectCopyExportError.UnsafeSourcePath,
					$"The effective tree contains an unsafe relative path: {relativePath}");
			}

			if (relativePaths.Add(relativePath))
				entries.Add(new ProjectCopyExportPlanEntry(sourcePath, relativePath, node.IsDirectory));
		}

		entries.Sort(CompareEntries);
		return new ProjectCopyExportPlan(rootPath, projectName, entries);
	}

	private static int CompareEntries(ProjectCopyExportPlanEntry left, ProjectCopyExportPlanEntry right)
	{
		if (left.RelativePath.Length == 0)
			return right.RelativePath.Length == 0 ? 0 : -1;
		if (right.RelativePath.Length == 0)
			return 1;

		var directoryOrder = right.IsDirectory.CompareTo(left.IsDirectory);
		return directoryOrder != 0
			? directoryOrder
			: PathComparer.Default.Compare(left.RelativePath, right.RelativePath);
	}

	private static bool ContainsParentTraversal(string relativePath)
	{
		if (string.IsNullOrEmpty(relativePath))
			return false;

		return relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
			.Any(static segment => segment == "..");
	}

	public static string NormalizeProjectName(string projectName, string rootPath)
	{
		var candidate = string.IsNullOrWhiteSpace(projectName)
			? Path.GetFileName(rootPath)
			: projectName.Trim();
		if (string.IsNullOrWhiteSpace(candidate))
			candidate = "project";

		var invalid = Path.GetInvalidFileNameChars().ToHashSet();
		foreach (var character in "<>:\"/\\|?*")
			invalid.Add(character);

		var sanitized = new StringBuilder(candidate.Length);
		foreach (var character in candidate)
			sanitized.Append(invalid.Contains(character) || char.IsControl(character) ? '_' : character);

		var result = sanitized.ToString().Trim().TrimEnd('.', ' ');
		return string.IsNullOrWhiteSpace(result) ? "project" : result;
	}

	private static ProjectCopyExportException InvalidRequest(string message) =>
		new(ProjectCopyExportError.InvalidRequest, message);
}
