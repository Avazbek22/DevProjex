namespace DevProjex.Application.Dependencies;

/// <summary>
/// Finds the test files of a source file among the files a caller may name, from common naming
/// conventions and, when an index is supplied, from resolved dependents that are test files.
/// All comparisons are ordinal, so a name differing only in case is not a match.
/// </summary>
public static class DependencyTestFiles
{
	private static readonly string[] TestSegments = ["test", "tests", "__tests__", "spec"];

	public static bool IsTestPath(string relativePath)
	{
		ArgumentNullException.ThrowIfNull(relativePath);
		var segments = relativePath.Split('/');
		for (var index = 0; index < segments.Length - 1; index++)
		{
			if (TestSegments.Contains(segments[index], StringComparer.Ordinal))
				return true;
		}
		var fileName = segments[^1];
		var extension = Path.GetExtension(fileName);
		var stem = Path.GetFileNameWithoutExtension(fileName);
		return stem.EndsWith(".test", StringComparison.Ordinal) ||
			stem.EndsWith(".spec", StringComparison.Ordinal) ||
			extension == ".py" && (stem.StartsWith("test_", StringComparison.Ordinal) ||
				stem.EndsWith("_test", StringComparison.Ordinal)) ||
			extension == ".go" && stem.EndsWith("_test", StringComparison.Ordinal) ||
			extension == ".cs" && stem.EndsWith("Tests", StringComparison.Ordinal) ||
			extension == ".java" && stem.EndsWith("Test", StringComparison.Ordinal);
	}

	/// <summary>
	/// Up to <paramref name="maximum"/> test files of <paramref name="sourcePath"/> in ordinal order,
	/// or none when the source is itself a test file. Every returned path is one of
	/// <paramref name="candidateFiles"/>.
	/// </summary>
	public static IReadOnlyList<string> Find(
		string sourcePath,
		IReadOnlyCollection<string> candidateFiles,
		DependencyIndexSnapshot? index,
		int maximum)
	{
		ArgumentNullException.ThrowIfNull(sourcePath);
		ArgumentNullException.ThrowIfNull(candidateFiles);
		if (maximum <= 0 || IsTestPath(sourcePath))
			return [];

		var found = new SortedSet<string>(StringComparer.Ordinal);
		foreach (var candidate in candidateFiles)
		{
			if (!string.Equals(candidate, sourcePath, StringComparison.Ordinal) &&
				MatchesConvention(sourcePath, candidate))
				found.Add(candidate);
		}
		if (index is not null)
		{
			var allowed = candidateFiles as IReadOnlySet<string> ??
				candidateFiles.ToHashSet(StringComparer.Ordinal);
			foreach (var dependent in ResolvedDependents(index, sourcePath))
			{
				if (IsTestPath(dependent) && allowed.Contains(dependent))
					found.Add(dependent);
			}
		}
		return found.Take(maximum).ToArray();
	}

	/// <summary>Distinct resolved dependents of one file, excluding the file itself.</summary>
	public static IEnumerable<string> ResolvedDependents(DependencyIndexSnapshot index, string targetPath)
	{
		ArgumentNullException.ThrowIfNull(index);
		if (!index.EdgesByTarget.TryGetValue(targetPath, out var edges))
			yield break;
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var edge in edges)
		{
			if (edge.Status == ResolutionStatus.Resolved &&
				!string.Equals(edge.Source, targetPath, StringComparison.Ordinal) &&
				seen.Add(edge.Source))
				yield return edge.Source;
		}
	}

	private static bool MatchesConvention(string sourcePath, string candidate)
	{
		var sourceDirectory = DirectoryOf(sourcePath);
		var sourceFileName = FileNameOf(sourcePath);
		var extension = Path.GetExtension(sourceFileName);
		var stem = Path.GetFileNameWithoutExtension(sourceFileName);
		if (stem.Length == 0)
			return false;
		var candidateDirectory = DirectoryOf(candidate);
		var candidateFileName = FileNameOf(candidate);

		if (string.Equals(candidateDirectory, sourceDirectory, StringComparison.Ordinal))
		{
			if (candidateFileName == stem + ".test" + extension || candidateFileName == stem + ".spec" + extension)
				return true;
			if (extension == ".py" && candidateFileName == "test_" + sourceFileName)
				return true;
			if (extension == ".go" && candidateFileName == stem + "_test.go")
				return true;
		}
		if (string.Equals(candidateDirectory, Combine(sourceDirectory, "__tests__"), StringComparison.Ordinal) &&
			candidateFileName.StartsWith(stem + ".", StringComparison.Ordinal))
			return true;
		if (extension == ".py" && HasSegment(candidateDirectory, "tests"))
		{
			// A private module such as _config.py is conventionally tested by test_config.py.
			var publicStem = stem.TrimStart('_');
			if (candidateFileName == "test_" + stem + ".py" ||
				publicStem.Length > 0 && candidateFileName == "test_" + publicStem + ".py")
				return true;
		}
		return extension == ".cs" && candidateFileName == stem + "Tests.cs" ||
			extension == ".java" && candidateFileName == stem + "Test.java";
	}

	private static bool HasSegment(string directory, string segment) =>
		directory.Split('/').Contains(segment, StringComparer.Ordinal);

	private static string DirectoryOf(string path)
	{
		var separator = path.LastIndexOf('/');
		return separator < 0 ? string.Empty : path[..separator];
	}

	private static string FileNameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

	private static string Combine(string directory, string name) =>
		directory.Length == 0 ? name : directory + "/" + name;
}
