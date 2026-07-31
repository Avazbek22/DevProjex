namespace DevProjex.Tests.Integration;

public sealed class SmartIgnoreEvidenceContractMatrixIntegrationTests
{
	[Fact]
	public void AllStackRules_ProjectMarkerAloneNeverHidesSourceLookalikeDirectories()
	{
		var cases = new[]
		{
			new StackFolderCase("frontend-build", "package.json", "build", "docs/guide.md", new FrontendArtifactsIgnoreRule()),
			new StackFolderCase("frontend-dist", "package.json", "dist", "schema.graphql", new FrontendArtifactsIgnoreRule()),
			new StackFolderCase("frontend-coverage", "package.json", "coverage", "docs/guide.md", new FrontendArtifactsIgnoreRule()),
			new StackFolderCase("frontend-cache", "package.json", ".cache", "source.txt", new FrontendArtifactsIgnoreRule()),
			new StackFolderCase("frontend-out", "package.json", "out", "source.ts", new FrontendArtifactsIgnoreRule()),
			new StackFolderCase("dotnet-bin", "App.csproj", "bin", "tools/source.cs", new DotNetArtifactsIgnoreRule()),
			new StackFolderCase("dotnet-obj", "App.csproj", "obj", "models/source.cs", new DotNetArtifactsIgnoreRule()),
			new StackFolderCase("jvm-build", "settings.gradle", "build", "source.kt", new JvmArtifactsIgnoreRule()),
			new StackFolderCase("jvm-out", "settings.gradle", "out", "source.kt", new JvmArtifactsIgnoreRule()),
			new StackFolderCase("jvm-target", "pom.xml", "target", "source.java", new JvmArtifactsIgnoreRule()),
			new StackFolderCase("rust-target", "Cargo.toml", "target", "source.rs", new RustArtifactsIgnoreRule()),
			new StackFolderCase("python-venv", "pyproject.toml", "venv", "source.py", new PythonArtifactsIgnoreRule()),
			new StackFolderCase("python-env", "pyproject.toml", "env", "source.py", new PythonArtifactsIgnoreRule()),
			new StackFolderCase("go-vendor", "go.mod", "vendor", "source.go", new GoArtifactsIgnoreRule()),
			new StackFolderCase("go-bin", "go.mod", "bin", "build-script.sh", new GoArtifactsIgnoreRule()),
			new StackFolderCase("php-vendor", "composer.json", "vendor", "source.php", new PhpArtifactsIgnoreRule()),
			new StackFolderCase("ruby-vendor", "Gemfile", "vendor", "source.rb", new RubyArtifactsIgnoreRule()),
			new StackFolderCase("ruby-log", "Gemfile", "log", "README.md", new RubyArtifactsIgnoreRule()),
			new StackFolderCase("ruby-log-fixture", "Gemfile", "log", "fixture.log", new RubyArtifactsIgnoreRule()),
			new StackFolderCase("ruby-tmp", "Gemfile", "tmp", "source.rb", new RubyArtifactsIgnoreRule())
		};

		foreach (var testCase in cases)
		{
			using var project = new TemporaryDirectory();
			project.CreateFile(testCase.MarkerFile, MarkerContent(testCase.MarkerFile));
			project.CreateFile(Path.Combine(testCase.FolderName, testCase.RelativeFile), "hand-written source\n");
			var rules = CreateRules(project.Path, testCase.Rule);

			Assert.False(
				rules.IsSmartIgnoredDirectory(
					Path.Combine(project.Path, testCase.FolderName),
					testCase.FolderName),
				$"{testCase.Name}: a marker plus a common directory name is not artifact evidence.");

			var tree = BuildCompleteTree(project.Path, rules);
			Assert.True(
				ContainsPath(tree.Root, $"{testCase.FolderName}/{testCase.RelativeFile}"),
				$"{testCase.Name}: source lookalike disappeared from the effective tree.");
		}
	}

	[Fact]
	public void AllStackRules_StrongLocalSignaturesHideConfirmedArtifacts()
	{
		var cases = new[]
		{
			new StackFolderCase("frontend-node-modules", "package.json", "node_modules", "package-lock.json", new FrontendArtifactsIgnoreRule()),
			new StackFolderCase("frontend-dist", "package.json", "dist", "manifest.json", new FrontendArtifactsIgnoreRule()),
			new StackFolderCase("frontend-build", "package.json", "build", "asset-manifest.json", new FrontendArtifactsIgnoreRule()),
			new StackFolderCase("frontend-coverage", "package.json", "coverage", "lcov.info", new FrontendArtifactsIgnoreRule()),
			new StackFolderCase("frontend-cache", "package.json", ".cache", "CACHEDIR.TAG", new FrontendArtifactsIgnoreRule()),
			new StackFolderCase("frontend-out", "package.json", "out", "_next/static/chunk.js", new FrontendArtifactsIgnoreRule()),
			new StackFolderCase("dotnet-bin", "App.csproj", "bin", "App.dll", new DotNetArtifactsIgnoreRule()),
			new StackFolderCase("dotnet-obj", "App.csproj", "obj", "project.assets.json", new DotNetArtifactsIgnoreRule()),
			new StackFolderCase("python-venv", "pyproject.toml", ".venv", "pyvenv.cfg", new PythonArtifactsIgnoreRule()),
			new StackFolderCase("python-plain-venv", "pyproject.toml", "venv", "pyvenv.cfg", new PythonArtifactsIgnoreRule()),
			new StackFolderCase("python-env", "pyproject.toml", "env", "pyvenv.cfg", new PythonArtifactsIgnoreRule()),
			new StackFolderCase("python-cache", "pyproject.toml", "__pycache__", "app.pyc", new PythonArtifactsIgnoreRule()),
			new StackFolderCase("jvm-build", "settings.gradle", "build", "classes/App.class", new JvmArtifactsIgnoreRule()),
			new StackFolderCase("jvm-out", "settings.gradle", "out", "classes/App.class", new JvmArtifactsIgnoreRule()),
			new StackFolderCase("jvm-target", "pom.xml", "target", "classes/App.class", new JvmArtifactsIgnoreRule()),
			new StackFolderCase("rust-target", "Cargo.toml", "target", "debug/app", new RustArtifactsIgnoreRule()),
			new StackFolderCase("go-vendor", "go.mod", "vendor", "modules.txt", new GoArtifactsIgnoreRule()),
			new StackFolderCase(
				"go-bin",
				"go.mod",
				"bin",
				"tool",
				new GoArtifactsIgnoreRule(),
				[0x7f, (byte)'E', (byte)'L', (byte)'F', 0x02, 0x01, 0x01, 0x00]),
			new StackFolderCase("php-vendor", "composer.json", "vendor", "autoload.php", new PhpArtifactsIgnoreRule()),
			new StackFolderCase(
				"ruby-vendor",
				"Gemfile",
				"vendor",
				"bundle/ruby/3.3.0/specifications/example.gemspec",
				new RubyArtifactsIgnoreRule()),
			new StackFolderCase("ruby-log", "Gemfile", "log", "development.log", new RubyArtifactsIgnoreRule()),
			new StackFolderCase("ruby-tmp", "Gemfile", "tmp", "CACHEDIR.TAG", new RubyArtifactsIgnoreRule())
		};

		foreach (var testCase in cases)
		{
			using var project = new TemporaryDirectory();
			project.CreateFile(testCase.MarkerFile, MarkerContent(testCase.MarkerFile));
			var relativeArtifactPath = Path.Combine(testCase.FolderName, testCase.RelativeFile);
			if (testCase.FileBytes is null)
			{
				project.CreateFile(relativeArtifactPath, "generated\n");
			}
			else
			{
				var fullArtifactPath = Path.Combine(project.Path, relativeArtifactPath);
				Directory.CreateDirectory(Path.GetDirectoryName(fullArtifactPath)!);
				File.WriteAllBytes(fullArtifactPath, testCase.FileBytes);
			}
			var rules = CreateRules(project.Path, testCase.Rule);

			Assert.True(
				rules.IsSmartIgnoredDirectory(
					Path.Combine(project.Path, testCase.FolderName),
					testCase.FolderName),
				$"{testCase.Name}: a confirmed artifact was not excluded.");

			var tree = BuildCompleteTree(project.Path, rules);
			Assert.False(
				ContainsPath(tree.Root, testCase.FolderName),
				$"{testCase.Name}: confirmed artifact leaked into the effective tree.");
		}
	}

	public static TheoryData<byte[]> NativeExecutableHeaders => new()
	{
		new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F' },
		new byte[] { (byte)'M', (byte)'Z', 0x90, 0x00 },
		new byte[] { 0xfe, 0xed, 0xfa, 0xcf },
		new byte[] { 0xcf, 0xfa, 0xed, 0xfe },
		new byte[] { 0xca, 0xfe, 0xba, 0xbe }
	};

	[Theory]
	[MemberData(nameof(NativeExecutableHeaders))]
	public void GoBin_NativeExecutableHeadersArePortableArtifactEvidence(byte[] header)
	{
		using var project = new TemporaryDirectory();
		project.CreateFile("go.mod", "module example.test/fixture\n");
		var binPath = project.CreateDirectory("bin");
		File.WriteAllBytes(Path.Combine(binPath, "tool"), [.. header, 0x00, 0x01]);
		var rules = CreateRules(project.Path, new GoArtifactsIgnoreRule());

		Assert.True(rules.IsSmartIgnoredDirectory(binPath, "bin"));
		Assert.False(ContainsPath(BuildCompleteTree(project.Path, rules).Root, "bin"));
	}

	[Fact]
	public void GoBin_PlainExtensionlessSourceToolRemainsVisible()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile("go.mod", "module example.test/fixture\n");
		project.CreateFile("bin/tool", "#!/bin/sh\necho source helper\n");
		var rules = CreateRules(project.Path, new GoArtifactsIgnoreRule());

		Assert.False(rules.IsSmartIgnoredDirectory(Path.Combine(project.Path, "bin"), "bin"));
		Assert.True(ContainsPath(BuildCompleteTree(project.Path, rules).Root, "bin/tool"));
	}

	[Fact]
	public void ScanSections_WeakAndConfirmedFoldersKeepRootsExtensionsCountsAndTreeConsistent()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile("package.json", "{}\n");
		project.CreateFile("src/app.ts", "export const app = true;\n");
		project.CreateFile("build/docs/guide.md", "hand-written documentation\n");
		project.CreateFile("dist/manifest.json", "{}\n");
		project.CreateFile("dist/only.generated", "generated\n");
		var rules = CreateRules(project.Path, new FrontendArtifactsIgnoreRule());
		var scanner = new ScanOptionsUseCase(new FileSystemScanner());

		var roots = scanner.GetRootFolders(
			project.Path,
			rules,
			TestContext.Current.CancellationToken).Value;
		var extensions = scanner.GetExtensionsForRootFolders(
			project.Path,
			["src", "build", "dist"],
			rules,
			TestContext.Current.CancellationToken).Value;
		var snapshot = scanner.GetIgnoreSectionSnapshotForRootFolders(
			project.Path,
			["src", "build", "dist"],
			extensionDiscoveryRules: rules,
			effectiveRules: rules,
			effectiveAllowedExtensions: null,
			includeDirectoryToggleProbeRoots: true,
			cancellationToken: TestContext.Current.CancellationToken).Value;
		var tree = BuildCompleteTree(project.Path, rules);

		Assert.Contains("src", roots, PathComparer.Default);
		Assert.Contains("build", roots, PathComparer.Default);
		Assert.DoesNotContain("dist", roots, PathComparer.Default);
		Assert.Contains(".ts", extensions);
		Assert.Contains(".md", extensions);
		Assert.DoesNotContain(".generated", extensions);
		Assert.Contains(".ts", snapshot.Extensions);
		Assert.Contains(".md", snapshot.Extensions);
		Assert.DoesNotContain(".generated", snapshot.Extensions);
		Assert.True(ContainsPath(tree.Root, "build/docs/guide.md"));
		Assert.True(ContainsPath(tree.Root, "src/app.ts"));
		Assert.False(ContainsPath(tree.Root, "dist"));
	}

	[Fact]
	public void NestedProjectBoundary_ParentStackNeverOwnsWeakFolderInsideChildStack()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile("package.json", "{}\n");
		project.CreateFile("services/api/Api.csproj", "<Project />\n");
		project.CreateFile("services/api/build/DomainModel.cs", "public sealed class DomainModel {}\n");
		project.CreateFile("services/api/dist/contracts.json", "{\"source\":true}\n");
		var rulesService = new IgnoreRulesService(new SmartIgnoreService([
			new FrontendArtifactsIgnoreRule(),
			new DotNetArtifactsIgnoreRule()
		]));
		var rules = rulesService.Build(project.Path, [IgnoreOptionId.SmartIgnore], selectedRootFolders: []);

		var tree = BuildCompleteTree(project.Path, rules);

		Assert.True(ContainsPath(tree.Root, "services/api/build/DomainModel.cs"));
		Assert.True(ContainsPath(tree.Root, "services/api/dist/contracts.json"));
	}

	[Fact]
	public void MutableArtifactEvidenceIsReevaluatedWithoutRebuildingRules()
	{
		using var project = new TemporaryDirectory();
		project.CreateFile("package.json", "{}\n");
		project.CreateFile("build/docs/guide.md", "hand-written documentation\n");
		var buildPath = Path.Combine(project.Path, "build");
		var rules = CreateRules(project.Path, new FrontendArtifactsIgnoreRule());

		Assert.False(rules.IsSmartIgnoredDirectory(buildPath, "build"));

		var evidencePath = project.CreateFile("build/asset-manifest.json", "{}\n");
		Assert.True(rules.IsSmartIgnoredDirectory(buildPath, "build"));

		File.Delete(evidencePath);
		Assert.False(rules.IsSmartIgnoredDirectory(buildPath, "build"));
	}

	[Fact]
	public void CandidateNamedNestedProjectsOwnTheirScopeBeforeParentArtifactRules()
	{
		var cases = new[]
		{
			new NestedProjectCase("rust-target-frontend", "Cargo.toml", "target", "package.json", "src/app.ts"),
			new NestedProjectCase("dotnet-bin-go", "App.csproj", "bin", "go.mod", "cmd/app/main.go"),
			new NestedProjectCase("jvm-target-frontend", "settings.gradle", "target", "package.json", "src/index.ts")
		};

		foreach (var testCase in cases)
		{
			using var project = new TemporaryDirectory();
			project.CreateFile(testCase.ParentMarker, MarkerContent(testCase.ParentMarker));
			project.CreateFile(
				Path.Combine(testCase.CandidateFolder, testCase.ChildMarker),
				MarkerContent(testCase.ChildMarker));
			project.CreateFile(
				Path.Combine(testCase.CandidateFolder, testCase.SourceFile),
				"hand-written source\n");
			var rules = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService().Build(
				project.Path,
				[IgnoreOptionId.SmartIgnore],
				selectedRootFolders: []);

			Assert.False(
				rules.IsSmartIgnoredDirectory(
					Path.Combine(project.Path, testCase.CandidateFolder),
					testCase.CandidateFolder),
				$"{testCase.Name}: the parent stack hid a nested project root.");
			Assert.True(
				ContainsPath(
					BuildCompleteTree(project.Path, rules).Root,
					$"{testCase.CandidateFolder}/{testCase.SourceFile.Replace('\\', '/') }"),
				$"{testCase.Name}: nested project source disappeared from the effective tree.");
		}
	}

	private static IgnoreRules CreateRules(string rootPath, ISmartIgnoreRule rule) =>
		new IgnoreRulesService(new SmartIgnoreService([rule])).Build(
			rootPath,
			[IgnoreOptionId.SmartIgnore],
			selectedRootFolders: []);

	private static TreeBuildResult BuildCompleteTree(string rootPath, IgnoreRules rules)
	{
		var extensions = Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
			.Select(static path => Path.GetExtension(path))
			.Where(static extension => !string.IsNullOrWhiteSpace(extension))
			.Select(static extension => extension!)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var roots = Directory.EnumerateDirectories(rootPath, "*", SearchOption.TopDirectoryOnly)
			.Select(Path.GetFileName)
			.Where(static name => !string.IsNullOrWhiteSpace(name))
			.Select(static name => name!)
			.ToHashSet(PathComparer.Default);

		return new TreeBuilder().Build(
			rootPath,
			new TreeFilterOptions(extensions, roots, rules),
			TestContext.Current.CancellationToken);
	}

	private static bool ContainsPath(FileSystemNode root, string relativePath)
	{
		var current = root;
		foreach (var segment in relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
		{
			var next = current.Children.FirstOrDefault(child =>
				string.Equals(child.Name, segment, StringComparison.Ordinal));
			if (next is null)
				return false;

			current = next;
		}

		return true;
	}

	private static string MarkerContent(string markerFile) => markerFile switch
	{
		"App.csproj" => "<Project />\n",
		"pyproject.toml" => "[project]\nname = \"fixture\"\n",
		"Cargo.toml" => "[package]\nname = \"fixture\"\n",
		"go.mod" => "module example.test/fixture\n",
		_ => "{}\n"
	};

	private sealed record StackFolderCase(
		string Name,
		string MarkerFile,
		string FolderName,
		string RelativeFile,
		ISmartIgnoreRule Rule,
		byte[]? FileBytes = null);

	private sealed record NestedProjectCase(
		string Name,
		string ParentMarker,
		string CandidateFolder,
		string ChildMarker,
		string SourceFile);
}
