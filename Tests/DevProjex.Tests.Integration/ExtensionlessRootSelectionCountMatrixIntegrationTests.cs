namespace DevProjex.Tests.Integration;

public sealed class ExtensionlessRootSelectionCountMatrixIntegrationTests
{
	[Theory]
	[MemberData(nameof(CountCases))]
	public void GetExtensionsForRootFolders_CountMatrix_IsStableForSelectedRoots(
		int caseId,
		int rootMode,
		bool ignoreExtensionlessFiles,
		int expectedExtensionlessCount)
	{
		_ = caseId;
		using var temp = new TemporaryDirectory();
		SeedWorkspace(temp.Path);

		var scanner = new FileSystemScanner();
		var useCase = new ScanOptionsUseCase(scanner);
		var rules = CreateRules(ignoreExtensionlessFiles);
		var selectedRoots = BuildSelectedRoots(rootMode);

		var result = useCase.GetExtensionsForRootFolders(temp.Path, selectedRoots, rules, cancellationToken: TestContext.Current.CancellationToken);
		var extensionlessCount = CountExtensionlessTokens(result.Value);

		Assert.Equal(expectedExtensionlessCount, extensionlessCount);
	}

	public static IEnumerable<object[]> CountCases()
	{
		var expectedByRootMode = new Dictionary<int, int>
		{
			[0] = 2, // root only: Dockerfile + README
			[1] = 3, // + src: Makefile
			[2] = 3, // + tests: LICENSE
			[3] = 4, // + src + tests: Makefile + LICENSE
			[4] = 2, // + docs: README is duplicated token
			[5] = 3, // + src + docs
			[6] = 2  // missing folder contributes nothing
		};

		var caseId = 0;
		foreach (var rootMode in expectedByRootMode.Keys.OrderBy(key => key))
		{
			foreach (var ignoreExtensionlessFiles in new[] { false, true })
			{
				yield return
				[
					caseId++,
					rootMode,
					ignoreExtensionlessFiles,
					ignoreExtensionlessFiles ? 0 : expectedByRootMode[rootMode]
				];
			}
		}
	}

	private static void SeedWorkspace(string rootPath)
	{
		Directory.CreateDirectory(Path.Combine(rootPath, "src"));
		Directory.CreateDirectory(Path.Combine(rootPath, "tests"));
		Directory.CreateDirectory(Path.Combine(rootPath, "docs"));

		File.WriteAllText(Path.Combine(rootPath, "Dockerfile"), "FROM mcr.microsoft.com/dotnet/runtime:10.0");
		File.WriteAllText(Path.Combine(rootPath, "README"), "Root readme");
		File.WriteAllText(Path.Combine(rootPath, "app.cs"), "class App { }");
		File.WriteAllText(Path.Combine(rootPath, ".env"), "ASPNETCORE_ENVIRONMENT=Development");

		File.WriteAllText(Path.Combine(rootPath, "src", "Dockerfile"), "FROM node:22");
		File.WriteAllText(Path.Combine(rootPath, "src", "Makefile"), "build:\n\tdotnet build");
		File.WriteAllText(Path.Combine(rootPath, "src", "module.ts"), "export const x = 1;");

		File.WriteAllText(Path.Combine(rootPath, "tests", "LICENSE"), "GPL-3.0");
		File.WriteAllText(Path.Combine(rootPath, "tests", "test.cs"), "class Tests { }");

		File.WriteAllText(Path.Combine(rootPath, "docs", "README"), "Docs readme");
		File.WriteAllText(Path.Combine(rootPath, "docs", "guide.md"), "# Guide");
	}

	private static IReadOnlyCollection<string> BuildSelectedRoots(int mode)
	{
		return mode switch
		{
			0 => Array.Empty<string>(),
			1 => new[] { "src" },
			2 => new[] { "tests" },
			3 => new[] { "src", "tests" },
			4 => new[] { "docs" },
			5 => new[] { "src", "docs" },
			6 => new[] { "missing-folder" },
			_ => Array.Empty<string>()
		};
	}

	private static IgnoreRules CreateRules(bool ignoreExtensionlessFiles) => new(
		IgnoreHiddenFolders: false,
		IgnoreHiddenFiles: false,
		IgnoreDotFolders: false,
		IgnoreDotFiles: false,
		SmartIgnoredFolders: new HashSet<string>(),
		SmartIgnoredFiles: new HashSet<string>())
	{
		IgnoreExtensionlessFiles = ignoreExtensionlessFiles
	};

	private static int CountExtensionlessTokens(IEnumerable<string> tokens)
	{
		var count = 0;
		foreach (var token in tokens)
		{
			if (string.IsNullOrWhiteSpace(token))
				continue;

			var extension = Path.GetExtension(token);
			if (string.IsNullOrEmpty(extension) || extension == ".")
				count++;
		}

		return count;
	}
}
