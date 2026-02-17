using DevProjex.Infrastructure.FileSystem;
using DevProjex.Tests.Integration.Helpers;

namespace DevProjex.Tests.Integration;

public sealed class ExtensionlessTreeBuilderMatrixIntegrationTests
{
	[Theory]
	[MemberData(nameof(TreeMatrixCases))]
	public void Build_ExtensionlessBehavior_Matrix(
		bool ignoreExtensionlessFiles,
		bool allowTxtExtension,
		FilterMode filterMode,
		bool nestedLocation,
		bool expectDockerfile,
		bool expectTxt)
	{
		using var temp = new TemporaryDirectory();

		var dockerfilePath = nestedLocation ? "src/Dockerfile" : "Dockerfile";
		var txtPath = nestedLocation ? "src/note.txt" : "note.txt";
		temp.CreateFile(dockerfilePath, "FROM dotnet");
		temp.CreateFile(txtPath, "note");

		var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (allowTxtExtension)
			allowedExtensions.Add(".txt");

		var allowedRootFolders = nestedLocation
			? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src" }
			: new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		var rules = new IgnoreRules(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(),
			SmartIgnoredFiles: new HashSet<string>())
		{
			IgnoreExtensionlessFiles = ignoreExtensionlessFiles
		};

		var options = new TreeFilterOptions(
			AllowedExtensions: allowedExtensions,
			AllowedRootFolders: allowedRootFolders,
			IgnoreRules: rules,
			NameFilter: ToNameFilter(filterMode));

		var builder = new TreeBuilder();
		var result = builder.Build(temp.Path, options);
		var fileNames = EnumerateFileNames(result.Root).ToHashSet(StringComparer.OrdinalIgnoreCase);

		Assert.Equal(expectDockerfile, fileNames.Contains("Dockerfile"));
		Assert.Equal(expectTxt, fileNames.Contains("note.txt"));
	}

	public static IEnumerable<object[]> TreeMatrixCases()
	{
		foreach (var ignoreExtensionlessFiles in new[] { false, true })
		foreach (var allowTxtExtension in new[] { false, true })
		foreach (var filterMode in Enum.GetValues<FilterMode>())
		foreach (var nestedLocation in new[] { false, true })
		{
			var expectDockerfile = !ignoreExtensionlessFiles &&
			                       (filterMode is FilterMode.None or FilterMode.DockerOnly);
			var expectTxt = allowTxtExtension && filterMode == FilterMode.None;

			yield return new object[]
			{
				ignoreExtensionlessFiles,
				allowTxtExtension,
				filterMode,
				nestedLocation,
				expectDockerfile,
				expectTxt
			};
		}
	}

	private static IEnumerable<string> EnumerateFileNames(FileSystemNode root)
	{
		foreach (var child in root.Children)
		{
			if (child.IsDirectory)
			{
				foreach (var nested in EnumerateFileNames(child))
					yield return nested;
				continue;
			}

			yield return child.Name;
		}
	}

	private static string? ToNameFilter(FilterMode mode) => mode switch
	{
		FilterMode.None => null,
		FilterMode.DockerOnly => "Docker",
		FilterMode.NoMatch => "zzzzzz",
		_ => null
	};

	public enum FilterMode
	{
		None,
		DockerOnly,
		NoMatch
	}
}
