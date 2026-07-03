namespace DevProjex.Tests.Integration;

public sealed class ExtensionlessRootSelectionIgnoreMatrixIntegrationTests
{
	[Theory]
	[MemberData(nameof(RootSelectionCases))]
	public void GetExtensionsForRootFolders_Matrix_RespectsExtensionlessAndDotFileRules(
		int caseId,
		int rootMode,
		bool ignoreExtensionlessFiles,
		bool ignoreDotFiles)
	{
		_ = caseId;
		using var temp = new TemporaryDirectory();
		SeedWorkspace(temp.Path);

		var scanner = new FileSystemScanner();
		var useCase = new ScanOptionsUseCase(scanner);
		var rules = CreateRules(ignoreExtensionlessFiles, ignoreDotFiles);
		var selectedRoots = BuildSelectedRoots(rootMode);

		var result = useCase.GetExtensionsForRootFolders(temp.Path, selectedRoots, rules, cancellationToken: TestContext.Current.CancellationToken);

		var expected = BuildExpectedTokensForSelectedRoots(
			rootMode,
			ignoreExtensionlessFiles,
			ignoreDotFiles);

		Assert.Equal(expected.Count, result.Value.Count);
		foreach (var token in expected)
			Assert.Contains(token, result.Value);
	}

	public static IEnumerable<object[]> RootSelectionCases()
	{
		var caseId = 0;
		var rootModes = new[] { 0, 1, 2, 3, 4, 5 };
		foreach (var rootMode in rootModes)
		{
			foreach (var ignoreExtensionlessFiles in new[] { false, true })
			{
				foreach (var ignoreDotFiles in new[] { false, true })
				{
					yield return
					[
						caseId++,
						rootMode,
						ignoreExtensionlessFiles,
						ignoreDotFiles
					];
				}
			}
		}
	}

	private static void SeedWorkspace(string rootPath)
	{
		Directory.CreateDirectory(Path.Combine(rootPath, "src"));
		Directory.CreateDirectory(Path.Combine(rootPath, "tests"));

		File.WriteAllText(Path.Combine(rootPath, "Dockerfile"), "FROM mcr.microsoft.com/dotnet/runtime:10.0");
		File.WriteAllText(Path.Combine(rootPath, ".env"), "ASPNETCORE_ENVIRONMENT=Development");
		File.WriteAllText(Path.Combine(rootPath, "app.cs"), "class App { }");

		File.WriteAllText(Path.Combine(rootPath, "src", "Makefile"), "build:\n\tdotnet build");
		File.WriteAllText(Path.Combine(rootPath, "src", ".eslintrc"), "{ \"root\": true }");
		File.WriteAllText(Path.Combine(rootPath, "src", "module.ts"), "export const x = 1;");

		File.WriteAllText(Path.Combine(rootPath, "tests", "LICENSE"), "MIT");
		File.WriteAllText(Path.Combine(rootPath, "tests", ".runsettings"), "<RunSettings />");
		File.WriteAllText(Path.Combine(rootPath, "tests", "test.cs"), "class Tests { }");
	}

	private static IReadOnlyCollection<string> BuildSelectedRoots(int mode)
	{
		return mode switch
		{
			0 => Array.Empty<string>(),
			1 => new[] { "src" },
			2 => new[] { "tests" },
			3 => new[] { "src", "tests" },
			4 => new[] { "missing-folder" },
			5 => new[] { "src", "missing-folder" },
			_ => Array.Empty<string>()
		};
	}

	private static HashSet<string> BuildExpectedTokensForSelectedRoots(
		int mode,
		bool ignoreExtensionlessFiles,
		bool ignoreDotFiles)
	{
		var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			".cs"
		};

		if (!ignoreExtensionlessFiles)
			expected.Add("Dockerfile");

		if (!ignoreDotFiles)
			expected.Add(".env");

		var includeSrc = mode is 1 or 3 or 5;
		var includeTests = mode is 2 or 3;

		if (includeSrc)
		{
			expected.Add(".ts");
			if (!ignoreExtensionlessFiles)
				expected.Add("Makefile");
			if (!ignoreDotFiles)
				expected.Add(".eslintrc");
		}

		if (includeTests)
		{
			expected.Add(".cs");
			if (!ignoreExtensionlessFiles)
				expected.Add("LICENSE");
			if (!ignoreDotFiles)
				expected.Add(".runsettings");
		}

		return expected;
	}

	private static IgnoreRules CreateRules(bool ignoreExtensionlessFiles, bool ignoreDotFiles) => new(
		IgnoreHiddenFolders: false,
		IgnoreHiddenFiles: false,
		IgnoreDotFolders: false,
		IgnoreDotFiles: ignoreDotFiles,
		SmartIgnoredFolders: new HashSet<string>(),
		SmartIgnoredFiles: new HashSet<string>())
	{
		IgnoreExtensionlessFiles = ignoreExtensionlessFiles
	};
}
