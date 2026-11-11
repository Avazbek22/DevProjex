namespace DevProjex.Application.Dependencies;

public static class DependencySeedFolders
{
	public static IReadOnlyList<string> DirectFiles(string directory, IEnumerable<string> selectedFiles) => selectedFiles
		.Where(path => PathComparer.Default.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(directory)))
		.Order(ProjectTreePathIdentity.CanonicalComparer).ToArray();
}
