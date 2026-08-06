using static DevProjex.Tests.Shared.ProjectLoadWorkflow.ProjectLoadWorkflowRefreshHarness;

namespace DevProjex.Tests.Integration;

public sealed class ScopedControllerGoldenMatrixIntegrationTests
{
	private const int MaximumConvergencePasses = 6;
	private static readonly string[] AllRootNames = ["api", "docs", "web"];

	// Live refresh has historically been the easiest place to drift because it starts
	// from cached UI state instead of a clean default load. Keep it pinned to full refresh.
	[Theory]
	[MemberData(nameof(ScopedGoldenCases))]
	public void LiveRefresh_AfterFullRefresh_KeepsGoldenDynamicSectionsStable(ScopedGoldenCase testCase)
	{
		using var workspace = CreateScopedControllerWorkspace();
		var services = CreateServices();
		var baseline = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			CreateDefaultContext(workspace.Path));
		var scenarioContext = BuildScenarioContext(workspace.Path, baseline, testCase);
		var fullSnapshot = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			scenarioContext);
		var liveSnapshot = services.Engine.ComputeLiveRefreshSnapshot(
			BuildConvergedContext(workspace.Path, fullSnapshot, scenarioContext),
			TestContext.Current.CancellationToken);

		AssertEquivalentDynamicSections(fullSnapshot, liveSnapshot, testCase);
		AssertGoldenSnapshot(liveSnapshot, testCase);
	}

	// Inventory projection is the fast path used by the app after scanning. It must stay
	// byte-for-byte equivalent to a direct TreeBuilder pass for the same effective rules.
	[Theory]
	[MemberData(nameof(InventoryGoldenCases))]
	public void CapturedInventory_ProjectsSameGoldenTreeAsDirectTreeBuilder(ScopedGoldenCase testCase)
	{
		using var workspace = CreateScopedControllerWorkspace();
		var services = CreateServices();
		var baseline = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			CreateDefaultContext(workspace.Path) with { CaptureTreeInventory = true });
		var snapshot = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			BuildScenarioContext(workspace.Path, baseline, testCase) with { CaptureTreeInventory = true });

		Assert.NotNull(snapshot.TreeInventory);
		var direct = BuildTreeFromSnapshot(workspace.Path, services, snapshot);
		var projected = BuildTreeFromSnapshot(workspace.Path, services, snapshot, snapshot.TreeInventory);

		Assert.Equal(FlattenTree(direct.Root), FlattenTree(projected.Root));
		AssertGoldenTree(workspace.Path, services, snapshot, testCase);
	}

	// Export is the last user-visible surface after ignore decisions. This pins the
	// clipboard payload to the same tree that SelectionRefreshEngine exposes.
	[Theory]
	[MemberData(nameof(ExportGoldenCases))]
	public async Task TreeAndContentExport_UsesOnlyGoldenVisibleFiles(ExportGoldenCase testCase)
	{
		using var workspace = CreateScopedControllerWorkspace();
		var services = CreateServices();
		var baseline = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			CreateDefaultContext(workspace.Path));
		var snapshot = ComputeConvergedSnapshot(
			services,
			workspace.Path,
			BuildScenarioContext(workspace.Path, baseline, testCase.Scenario));
		var tree = BuildTreeFromSnapshot(workspace.Path, services, snapshot);
		var descriptor = ToDescriptor(tree.Root);
		var export = await new TreeAndContentExportService(
				new TreeExportService(),
				new SelectedContentExportService(new FileContentAnalyzer()))
			.BuildAsync(
				workspace.Path,
				descriptor,
				new HashSet<string>(PathComparer.Default),
				TreeTextFormat.Ascii,
				TestContext.Current.CancellationToken);

		foreach (var fragment in testCase.ExpectedFragments)
			Assert.Contains(NormalizeExportFragment(fragment), export, StringComparison.Ordinal);
		foreach (var fragment in testCase.ForbiddenFragments)
			Assert.DoesNotContain(NormalizeExportFragment(fragment), export, StringComparison.Ordinal);
	}

	public static IEnumerable<object[]> ScopedGoldenCases()
	{
		yield return [CreateCase(
			"all roots defaults",
			roots: null,
			extensions: null,
			forcedStates: null,
			expectedVisibleExtensions: [".cs", ".csproj", ".gitignore", ".json", ".log", ".md", ".ts"],
			expectedIgnoreOptions:
			[
				ExpectedVisible(IgnoreOptionId.UseGitIgnore, true),
				ExpectedVisible(IgnoreOptionId.SmartIgnore, true),
				ExpectedVisible(IgnoreOptionId.DotFolders, true),
				ExpectedVisible(IgnoreOptionId.DotFiles, true)
			],
			expectedVisiblePaths:
			[
				"api/App.csproj",
				"api/src/Program.cs",
				"api/logs/keep.log",
				"api/generated/keep/keep.cs",
				"web/package.json",
				"web/src/app.ts",
				"docs/readme.md"
			],
			expectedHiddenPaths:
			[
				"api/.gitignore",
				"api/logs/drop.log",
				"api/logs/drop.tmp",
				"api/generated/drop/old.cs",
				"api/bin/Debug/api.dll",
				"api/.idea/settings.xml",
				"web/node_modules/pkg/index.js",
				"web/.cache/cache.json",
				"docs/.drafts/draft.md"
			],
			expectedCounts: new ExpectedCountContract(
				DotFolders: 2,
				DotFiles: 1,
				MinGitIgnoreImpact: 3,
				MinSmartIgnoreImpact: 2))];

		yield return [CreateCase(
			"all roots git off keeps smart and dot active",
			roots: null,
			extensions: null,
			forcedStates: States((IgnoreOptionId.UseGitIgnore, false)),
			expectedVisibleExtensions: [".cs", ".csproj", ".gitignore", ".json", ".log", ".md", ".tmp", ".ts"],
			expectedIgnoreOptions:
			[
				ExpectedVisible(IgnoreOptionId.UseGitIgnore, false),
				ExpectedVisible(IgnoreOptionId.SmartIgnore, true),
				ExpectedVisible(IgnoreOptionId.DotFolders, true),
				ExpectedVisible(IgnoreOptionId.DotFiles, true)
			],
			expectedVisiblePaths:
			[
				"api/logs/drop.log",
				"api/logs/drop.tmp",
				"api/generated/drop/old.cs",
				"api/generated/keep/keep.cs",
				"web/src/app.ts"
			],
			expectedHiddenPaths:
			[
				"api/.gitignore",
				"api/bin/Debug/api.dll",
				"api/.idea/settings.xml",
				"web/node_modules/pkg/index.js",
				"web/.cache/cache.json"
			],
			expectedCounts: new ExpectedCountContract(
				DotFolders: 2,
				DotFiles: 1,
				MinGitIgnoreImpact: 3,
				MinSmartIgnoreImpact: 2))];

		yield return [CreateCase(
			"all roots smart off keeps git and dot active",
			roots: null,
			extensions: null,
			forcedStates: States((IgnoreOptionId.SmartIgnore, false)),
			expectedVisibleExtensions: [".cs", ".csproj", ".dll", ".gitignore", ".js", ".json", ".log", ".md", ".ts"],
			expectedIgnoreOptions:
			[
				ExpectedVisible(IgnoreOptionId.UseGitIgnore, true),
				ExpectedVisible(IgnoreOptionId.SmartIgnore, false),
				ExpectedVisible(IgnoreOptionId.DotFolders, true),
				ExpectedVisible(IgnoreOptionId.DotFiles, true)
			],
			expectedVisiblePaths:
			[
				"api/bin/Debug/api.dll",
				"web/node_modules/pkg/index.js",
				"api/generated/keep/keep.cs"
			],
			expectedHiddenPaths:
			[
				"api/.gitignore",
				"api/logs/drop.log",
				"api/logs/drop.tmp",
				"api/generated/drop/old.cs",
				"api/.idea/settings.xml",
				"web/.cache/cache.json"
			],
			expectedCounts: new ExpectedCountContract(
				DotFolders: 3,
				DotFiles: 1,
				MinGitIgnoreImpact: 3,
				MinSmartIgnoreImpact: 2))];

		yield return [CreateCase(
			"all roots dot folders off keeps controllers active",
			roots: null,
			extensions: null,
			forcedStates: States((IgnoreOptionId.DotFolders, false)),
			expectedVisibleExtensions: [".cs", ".csproj", ".gitignore", ".json", ".log", ".md", ".ts", ".xml"],
			expectedIgnoreOptions:
			[
				ExpectedVisible(IgnoreOptionId.UseGitIgnore, true),
				ExpectedVisible(IgnoreOptionId.SmartIgnore, true),
				ExpectedVisible(IgnoreOptionId.DotFolders, false),
				ExpectedVisible(IgnoreOptionId.DotFiles, true)
			],
			expectedVisiblePaths:
			[
				"api/.idea/settings.xml",
				"docs/.drafts/draft.md"
			],
			expectedHiddenPaths:
			[
				"api/.gitignore",
				"api/logs/drop.tmp",
				"api/bin/Debug/api.dll",
				"web/node_modules/pkg/index.js",
				"web/.cache/cache.json"
			],
			expectedCounts: new ExpectedCountContract(
				DotFolders: 2,
				DotFiles: 1,
				MinGitIgnoreImpact: 3,
				MinSmartIgnoreImpact: 2))];

		yield return [CreateCase(
			"all roots all ignore off exposes every extension",
			roots: null,
			extensions: null,
			forcedStates: AllOffStates(),
			expectedVisibleExtensions: [".cs", ".csproj", ".dll", ".gitignore", ".js", ".json", ".log", ".md", ".tag", ".tmp", ".ts", ".xml"],
			expectedIgnoreOptions:
			[
				ExpectedVisible(IgnoreOptionId.UseGitIgnore, false),
				ExpectedVisible(IgnoreOptionId.SmartIgnore, false),
				ExpectedVisible(IgnoreOptionId.DotFolders, false),
				ExpectedVisible(IgnoreOptionId.DotFiles, false)
			],
			expectedVisiblePaths:
			[
				"api/.gitignore",
				"api/logs/drop.log",
				"api/logs/drop.tmp",
				"api/generated/drop/old.cs",
				"api/bin/Debug/api.dll",
				"api/.idea/settings.xml",
				"web/node_modules/pkg/index.js",
				"web/.cache/cache.json",
				"docs/.drafts/draft.md"
			],
			expectedHiddenPaths: [],
			expectedCounts: null)];

		yield return [CreateCase(
			"api root defaults keeps git and smart controllers independent",
			roots: ["api"],
			extensions: null,
			forcedStates: null,
			expectedVisibleExtensions: [".cs", ".csproj", ".gitignore", ".log"],
			expectedIgnoreOptions:
			[
				ExpectedVisible(IgnoreOptionId.UseGitIgnore, true),
				ExpectedVisible(IgnoreOptionId.SmartIgnore, true),
				ExpectedVisible(IgnoreOptionId.DotFolders, true),
				ExpectedVisible(IgnoreOptionId.DotFiles, true)
			],
			expectedVisiblePaths:
			[
				"api/App.csproj",
				"api/src/Program.cs",
				"api/logs/keep.log",
				"api/generated/keep/keep.cs"
			],
			expectedHiddenPaths:
			[
				"api/.gitignore",
				"api/logs/drop.log",
				"api/logs/drop.tmp",
				"api/generated/drop/old.cs",
				"api/bin/Debug/api.dll",
				"api/.idea/settings.xml",
				"web/package.json"
			],
			expectedCounts: new ExpectedCountContract(
				DotFolders: 1,
				DotFiles: 1,
				MinGitIgnoreImpact: 3,
				MinSmartIgnoreImpact: 1))];

		yield return [CreateCase(
			"api root git off keeps smart active",
			roots: ["api"],
			extensions: null,
			forcedStates: States((IgnoreOptionId.UseGitIgnore, false)),
			expectedVisibleExtensions: [".cs", ".csproj", ".gitignore", ".log", ".tmp"],
			expectedIgnoreOptions:
			[
				ExpectedVisible(IgnoreOptionId.UseGitIgnore, false),
				ExpectedVisible(IgnoreOptionId.SmartIgnore, true),
				ExpectedVisible(IgnoreOptionId.DotFolders, true),
				ExpectedVisible(IgnoreOptionId.DotFiles, true)
			],
			expectedVisiblePaths:
			[
				"api/logs/drop.log",
				"api/logs/drop.tmp",
				"api/generated/drop/old.cs"
			],
			expectedHiddenPaths:
			[
				"api/.gitignore",
				"api/.idea/settings.xml",
				"api/bin/Debug/api.dll",
				"web/package.json"
			],
			expectedCounts: new ExpectedCountContract(
				DotFolders: 1,
				DotFiles: 1,
				MinGitIgnoreImpact: 3,
				MinSmartIgnoreImpact: 1))];

		yield return [CreateCase(
			"api root cs extension preserves git negation branch",
			roots: ["api"],
			extensions: [".cs"],
			forcedStates: null,
			expectedVisibleExtensions: [".cs", ".csproj", ".gitignore", ".log"],
			expectedCheckedExtensions: [".cs"],
			expectedIgnoreOptions:
			[
				ExpectedVisible(IgnoreOptionId.UseGitIgnore, true),
				ExpectedVisible(IgnoreOptionId.SmartIgnore, true),
				ExpectedVisible(IgnoreOptionId.DotFolders, true)
			],
			expectedVisiblePaths:
			[
				"api/src/Program.cs",
				"api/generated/keep/keep.cs"
			],
			expectedHiddenPaths:
			[
				"api/generated/drop/old.cs",
				"api/logs/keep.log",
				"api/App.csproj"
			],
			expectedCounts: new ExpectedCountContract(
				DotFolders: 1,
				DotFiles: null,
				MinGitIgnoreImpact: 1,
				MinSmartIgnoreImpact: 1))];

		yield return [CreateCase(
			"api root cs extension with git off exposes ignored cs branch",
			roots: ["api"],
			extensions: [".cs"],
			forcedStates: States((IgnoreOptionId.UseGitIgnore, false)),
			expectedVisibleExtensions: [".cs", ".csproj", ".gitignore", ".log", ".tmp"],
			// Newly discovered extensions default to checked when opening a controller branch.
			expectedCheckedExtensions: [".cs", ".tmp"],
			expectedIgnoreOptions:
			[
				ExpectedVisible(IgnoreOptionId.UseGitIgnore, false),
				ExpectedVisible(IgnoreOptionId.SmartIgnore, true),
				ExpectedVisible(IgnoreOptionId.DotFolders, true)
			],
			expectedVisiblePaths:
			[
				"api/src/Program.cs",
				"api/generated/keep/keep.cs",
				"api/generated/drop/old.cs",
				"api/logs/drop.tmp"
			],
			expectedHiddenPaths:
			[
				"api/logs/keep.log",
				"api/App.csproj",
				"api/bin/Debug/api.dll"
			],
			expectedCounts: new ExpectedCountContract(
				DotFolders: 1,
				DotFiles: null,
				MinGitIgnoreImpact: 1,
				MinSmartIgnoreImpact: 1))];

		yield return [CreateCase(
			"api root dll extension remains absent while git controller is on",
			roots: ["api"],
			extensions: [".dll"],
			forcedStates: null,
			expectedVisibleExtensions: [".cs", ".csproj", ".gitignore", ".log"],
			expectedCheckedExtensions: [],
			expectedIgnoreOptions:
			[
				ExpectedVisible(IgnoreOptionId.UseGitIgnore, true),
				ExpectedVisible(IgnoreOptionId.SmartIgnore, true)
			],
			expectedVisiblePaths: [],
			expectedHiddenPaths: ["api/bin/Debug/api.dll"],
			expectedCheckedRoots: [],
			expectedCounts: new ExpectedCountContract(
				DotFolders: null,
				DotFiles: null,
				MinGitIgnoreImpact: 1,
				MinSmartIgnoreImpact: 1))];

		yield return [CreateCase(
			"api root dll extension remains absent when only git is off",
			roots: ["api"],
			extensions: [".dll"],
			forcedStates: States((IgnoreOptionId.UseGitIgnore, false)),
			expectedVisibleExtensions: [".cs", ".csproj", ".gitignore", ".log", ".tmp"],
			// Git-off reveals .tmp, while Smart Ignore continues to own the .dll artifact.
			expectedCheckedExtensions: [".tmp"],
			expectedIgnoreOptions:
			[
				ExpectedVisible(IgnoreOptionId.UseGitIgnore, false),
				ExpectedVisible(IgnoreOptionId.SmartIgnore, true)
			],
			expectedVisiblePaths: ["api/logs/drop.tmp"],
			expectedHiddenPaths:
			[
				"api/bin/Debug/api.dll",
				"api/logs/keep.log"
			],
			expectedCounts: new ExpectedCountContract(
				DotFolders: null,
				DotFiles: null,
				MinGitIgnoreImpact: 1,
				MinSmartIgnoreImpact: 1))];

		yield return [CreateCase(
			"api root tmp extension appears only after git is off",
			roots: ["api"],
			extensions: [".tmp"],
			forcedStates: States((IgnoreOptionId.UseGitIgnore, false)),
			expectedVisibleExtensions: [".cs", ".csproj", ".gitignore", ".log", ".tmp"],
			// Smart Ignore continues to own .dll while Git-off reveals the requested .tmp.
			expectedCheckedExtensions: [".tmp"],
			expectedIgnoreOptions:
			[
				ExpectedVisible(IgnoreOptionId.UseGitIgnore, false),
				ExpectedVisible(IgnoreOptionId.SmartIgnore, true)
			],
			expectedVisiblePaths:
			[
				"api/logs/drop.tmp"
			],
			expectedHiddenPaths:
			[
				"api/logs/keep.log",
				"api/generated/drop/old.cs",
				"api/bin/Debug/api.dll"
			],
			expectedCounts: new ExpectedCountContract(
				DotFolders: null,
				DotFiles: null,
				MinGitIgnoreImpact: 1,
				MinSmartIgnoreImpact: 1))];

		yield return [CreateCase(
			"web root defaults exposes only source and package extensions",
			roots: ["web"],
			extensions: null,
			forcedStates: null,
			expectedVisibleExtensions: [".json", ".ts"],
			expectedIgnoreOptions:
			[
				ExpectedHidden(IgnoreOptionId.UseGitIgnore),
				ExpectedVisible(IgnoreOptionId.SmartIgnore, true),
				ExpectedHidden(IgnoreOptionId.DotFolders)
			],
			expectedVisiblePaths:
			[
				"web/package.json",
				"web/src/app.ts"
			],
			expectedHiddenPaths:
			[
				"web/node_modules/pkg/index.js",
				"web/.cache/cache.json",
				"api/App.csproj"
			],
			expectedCounts: new ExpectedCountContract(
				DotFolders: 0,
				DotFiles: 0,
				MinGitIgnoreImpact: null,
				MinSmartIgnoreImpact: 1))];

		yield return [CreateCase(
			"web root js extension remains absent while smart is on",
			roots: ["web"],
			extensions: [".js"],
			forcedStates: null,
			expectedVisibleExtensions: [".json", ".ts"],
			expectedCheckedExtensions: [],
			expectedIgnoreOptions:
			[
				ExpectedHidden(IgnoreOptionId.UseGitIgnore),
				ExpectedVisible(IgnoreOptionId.SmartIgnore, true)
			],
			expectedVisiblePaths: [],
			expectedHiddenPaths: ["web/node_modules/pkg/index.js"],
			expectedCheckedRoots: [],
			expectedCounts: new ExpectedCountContract(
				DotFolders: null,
				DotFiles: null,
				MinGitIgnoreImpact: null,
				MinSmartIgnoreImpact: 1))];

		yield return [CreateCase(
			"web root js extension appears when smart is off",
			roots: ["web"],
			extensions: [".js"],
			forcedStates: States((IgnoreOptionId.SmartIgnore, false)),
			expectedVisibleExtensions: [".js", ".json", ".ts"],
			expectedCheckedExtensions: [".js"],
			expectedIgnoreOptions:
			[
				ExpectedHidden(IgnoreOptionId.UseGitIgnore),
				ExpectedVisible(IgnoreOptionId.SmartIgnore, false),
				ExpectedVisible(IgnoreOptionId.DotFolders, true)
			],
			expectedVisiblePaths: ["web/node_modules/pkg/index.js"],
			expectedHiddenPaths: ["web/package.json"],
			expectedCounts: new ExpectedCountContract(
				DotFolders: null,
				DotFiles: null,
				MinGitIgnoreImpact: null,
				MinSmartIgnoreImpact: 1))];

		yield return [CreateCase(
			"docs root defaults hides dot draft folder",
			roots: ["docs"],
			extensions: [".md"],
			forcedStates: null,
			expectedVisibleExtensions: [".md"],
			expectedCheckedExtensions: [".md"],
			expectedIgnoreOptions:
			[
				ExpectedHidden(IgnoreOptionId.UseGitIgnore),
				ExpectedHidden(IgnoreOptionId.SmartIgnore),
				ExpectedVisible(IgnoreOptionId.DotFolders, true)
			],
			expectedVisiblePaths: ["docs/readme.md"],
			expectedHiddenPaths: ["docs/.drafts/draft.md"],
			expectedCounts: new ExpectedCountContract(
				DotFolders: 1,
				DotFiles: 0,
				MinGitIgnoreImpact: null,
				MinSmartIgnoreImpact: null))];

		yield return [CreateCase(
			"docs root dot folders off exposes draft folder",
			roots: ["docs"],
			extensions: [".md"],
			forcedStates: States((IgnoreOptionId.DotFolders, false)),
			expectedVisibleExtensions: [".md"],
			expectedCheckedExtensions: [".md"],
			expectedIgnoreOptions:
			[
				ExpectedHidden(IgnoreOptionId.UseGitIgnore),
				ExpectedHidden(IgnoreOptionId.SmartIgnore),
				ExpectedVisible(IgnoreOptionId.DotFolders, false)
			],
			expectedVisiblePaths:
			[
				"docs/readme.md",
				"docs/.drafts/draft.md"
			],
			expectedHiddenPaths: [],
			expectedCounts: new ExpectedCountContract(
				DotFolders: 1,
				DotFiles: 0,
				MinGitIgnoreImpact: null,
				MinSmartIgnoreImpact: null))];

		yield return [CreateCase(
			"all roots xml extension is absent while dot folders are on",
			roots: null,
			extensions: [".xml"],
			forcedStates: null,
			expectedVisibleExtensions: [".cs", ".csproj", ".gitignore", ".json", ".log", ".md", ".ts"],
			expectedCheckedExtensions: [],
			expectedIgnoreOptions:
			[
				ExpectedHidden(IgnoreOptionId.DotFolders),
				ExpectedVisible(IgnoreOptionId.UseGitIgnore, true),
				ExpectedVisible(IgnoreOptionId.SmartIgnore, true)
			],
			expectedVisiblePaths: [],
			expectedHiddenPaths: ["api/.idea/settings.xml"],
			expectedCheckedRoots: [],
			expectedCounts: new ExpectedCountContract(
				DotFolders: 0,
				DotFiles: null,
				MinGitIgnoreImpact: 1,
				MinSmartIgnoreImpact: 1))];

		yield return [CreateCase(
			"all roots xml extension appears when dot folders are off",
			roots: null,
			extensions: [".xml"],
			forcedStates: States((IgnoreOptionId.DotFolders, false)),
			expectedVisibleExtensions: [".cs", ".csproj", ".gitignore", ".json", ".log", ".md", ".ts", ".xml"],
			expectedCheckedExtensions: [".xml"],
			expectedIgnoreOptions:
			[
				ExpectedVisible(IgnoreOptionId.DotFolders, false),
				ExpectedVisible(IgnoreOptionId.UseGitIgnore, true),
				ExpectedVisible(IgnoreOptionId.SmartIgnore, true)
			],
			expectedVisiblePaths: ["api/.idea/settings.xml"],
			expectedHiddenPaths: ["api/logs/drop.tmp"],
			expectedCheckedRoots: ["api"],
			expectedCounts: new ExpectedCountContract(
				DotFolders: 1,
				DotFiles: null,
				MinGitIgnoreImpact: 1,
				MinSmartIgnoreImpact: 1))];
	}

	public static IEnumerable<object[]> InventoryGoldenCases()
	{
		foreach (var data in ScopedGoldenCases())
		{
			var testCase = Assert.IsType<ScopedGoldenCase>(data[0]);
			if (testCase.Name is
			    "all roots defaults" or
			    "all roots all ignore off exposes every extension" or
			    "api root cs extension preserves git negation branch" or
			    "web root js extension appears when smart is off" or
			    "docs root dot folders off exposes draft folder")
			{
				yield return [testCase];
			}
		}
	}

	public static IEnumerable<object[]> ExportGoldenCases()
	{
		foreach (var data in ScopedGoldenCases())
		{
			var testCase = Assert.IsType<ScopedGoldenCase>(data[0]);
			if (testCase.Name == "all roots defaults")
			{
				yield return [new ExportGoldenCase(
					testCase,
					ExpectedFragments:
					[
						"api/logs/keep.log",
						"keep log",
						"web/src/app.ts",
						"export const ok = true;",
						"docs/readme.md",
						"# docs"
					],
					ForbiddenFragments:
					[
						"api/logs/drop.tmp",
						"drop temp",
						"web/node_modules/pkg/index.js",
						"module.exports",
						"docs/.drafts/draft.md",
						"# draft"
					])];
			}
			else if (testCase.Name == "api root cs extension preserves git negation branch")
			{
				yield return [new ExportGoldenCase(
					testCase,
					ExpectedFragments:
					[
						"api/src/Program.cs",
						"Console.WriteLine",
						"api/generated/keep/keep.cs",
						"class Keep"
					],
					ForbiddenFragments:
					[
						"api/generated/drop/old.cs",
						"class Dropped",
						"api/logs/keep.log",
						"keep log",
						"api/bin/Debug/api.dll"
					])];
			}
			else if (testCase.Name == "web root js extension appears when smart is off")
			{
				yield return [new ExportGoldenCase(
					testCase,
					ExpectedFragments:
					[
						"web/node_modules/pkg/index.js",
						"module.exports"
					],
					ForbiddenFragments:
					[
						"web/package.json",
						"web/src/app.ts",
						"export const ok"
					])];
			}
			else if (testCase.Name == "all roots all ignore off exposes every extension")
			{
				yield return [new ExportGoldenCase(
					testCase,
					ExpectedFragments:
					[
						"api/logs/drop.tmp",
						"drop temp",
						"api/generated/drop/old.cs",
						"class Dropped",
						"web/node_modules/pkg/index.js",
						"module.exports",
						"docs/.drafts/draft.md",
						"# draft"
					],
					ForbiddenFragments: [])];
			}
		}
	}

	private static TemporaryDirectory CreateScopedControllerWorkspace()
	{
		var workspace = new TemporaryDirectory();
		workspace.CreateFile("api/.gitignore", "logs/*\n!logs/keep.log\ngenerated/*\n!generated/keep/\n");
		workspace.CreateFile("api/App.csproj", "<Project />\n");
		workspace.CreateFile("api/src/Program.cs", "Console.WriteLine(\"api\");\n");
		workspace.CreateFile("api/logs/drop.log", "drop log\n");
		workspace.CreateFile("api/logs/drop.tmp", "drop temp\n");
		workspace.CreateFile("api/logs/keep.log", "keep log\n");
		workspace.CreateFile("api/generated/drop/old.cs", "class Dropped {}\n");
		workspace.CreateFile("api/generated/keep/keep.cs", "class Keep {}\n");
		workspace.CreateFile("api/bin/Debug/api.dll", "binary\n");
		workspace.CreateFile("api/.idea/settings.xml", "<settings />\n");
		workspace.CreateFile("web/package.json", "{ \"name\": \"web\" }\n");
		workspace.CreateFile("web/src/app.ts", "export const ok = true;\n");
		workspace.CreateFile("web/node_modules/pkg/index.js", "module.exports = {};\n");
		workspace.CreateFile("web/.cache/CACHEDIR.TAG", "Signature: 8a477f597d28d172789f06886806bc55\n");
		workspace.CreateFile("web/.cache/cache.json", "{}\n");
		workspace.CreateFile("docs/readme.md", "# docs\n");
		workspace.CreateFile("docs/.drafts/draft.md", "# draft\n");
		return workspace;
	}

	private static SelectionRefreshSnapshot ComputeConvergedSnapshot(
		WorkflowServices services,
		string rootPath,
		SelectionRefreshContext context)
	{
		var currentContext = context;
		var previous = services.Engine.ComputeFullRefreshSnapshot(currentContext, TestContext.Current.CancellationToken);
		for (var pass = 0; pass < MaximumConvergencePasses; pass++)
		{
			currentContext = BuildConvergedContext(rootPath, previous, currentContext) with
			{
				CaptureTreeInventory = context.CaptureTreeInventory
			};
			var next = services.Engine.ComputeFullRefreshSnapshot(
				currentContext,
				TestContext.Current.CancellationToken);
			if (SnapshotsMatch(previous, next))
				return next;

			previous = next;
		}

		currentContext = BuildConvergedContext(rootPath, previous, currentContext) with
		{
			CaptureTreeInventory = context.CaptureTreeInventory
		};
		var final = services.Engine.ComputeFullRefreshSnapshot(currentContext, TestContext.Current.CancellationToken);
		AssertEquivalentVisibleSnapshots(previous, final);
		return final;
	}

	private static bool SnapshotsMatch(SelectionRefreshSnapshot expected, SelectionRefreshSnapshot actual)
	{
		try
		{
			AssertEquivalentVisibleSnapshots(expected, actual);
			return true;
		}
		catch (Xunit.Sdk.XunitException)
		{
			return false;
		}
	}

	private static SelectionRefreshContext BuildScenarioContext(
		string rootPath,
		SelectionRefreshSnapshot baseline,
		ScopedGoldenCase testCase)
	{
		var context = CreateContextFromSnapshot(rootPath, baseline);
		if (testCase.SelectedRoots is not null)
			context = ApplyRootSelection(context, baseline, testCase.SelectedRoots);
		if (testCase.SelectedExtensions is not null)
			context = ApplyExtensionSelection(context, baseline, testCase.SelectedExtensions);
		if (testCase.ForcedIgnoreStates is not null)
			context = ApplyForcedIgnoreStates(context, testCase.ForcedIgnoreStates);

		return context;
	}

	private static SelectionRefreshContext ApplyRootSelection(
		SelectionRefreshContext context,
		SelectionRefreshSnapshot snapshot,
		IReadOnlySet<string> selectedRoots)
	{
		var rootStates = snapshot.RootOptions?.ToDictionary(
			static option => option.Name,
			option => selectedRoots.Contains(option.Name),
			PathComparer.Default) ?? new Dictionary<string, bool>(PathComparer.Default);

		return context with
		{
			AllRootFoldersChecked = false,
			RootSelectionInitialized = true,
			RootSelectionCache = new HashSet<string>(selectedRoots, PathComparer.Default),
			RootOptionStateCache = rootStates
		};
	}

	private static SelectionRefreshContext ApplyExtensionSelection(
		SelectionRefreshContext context,
		SelectionRefreshSnapshot snapshot,
		IReadOnlySet<string> selectedExtensions)
	{
		var extensionStates = snapshot.ExtensionOptions.ToDictionary(
			static option => option.Name,
			option => selectedExtensions.Contains(option.Name),
			StringComparer.OrdinalIgnoreCase);

		return context with
		{
			AllExtensionsChecked = false,
			ExtensionsSelectionInitialized = true,
			ExtensionsSelectionCache = new HashSet<string>(selectedExtensions, StringComparer.OrdinalIgnoreCase),
			ExtensionOptionStateCache = extensionStates
		};
	}

	private static SelectionRefreshContext ApplyForcedIgnoreStates(
		SelectionRefreshContext context,
		IReadOnlyDictionary<IgnoreOptionId, bool> forcedStates)
	{
		var stateCache = new Dictionary<IgnoreOptionId, bool>(context.IgnoreOptionStateCache);
		var selected = new HashSet<IgnoreOptionId>(context.IgnoreSelectionCache);
		foreach (var (optionId, isChecked) in forcedStates)
		{
			stateCache[optionId] = isChecked;
			if (isChecked)
				selected.Add(optionId);
			else
				selected.Remove(optionId);
		}

		return context with
		{
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = selected,
			IgnoreOptionStateCache = stateCache,
			IgnoreAllPreference = forcedStates.Values.All(static value => !value) ? false : null,
			IgnoreOptionStateCacheIsComplete = true
		};
	}

	private static void AssertGoldenSnapshot(
		SelectionRefreshSnapshot snapshot,
		ScopedGoldenCase testCase)
	{
		AssertSetEquals(
			testCase.ExpectedVisibleExtensions,
			snapshot.ExtensionOptions.Select(static option => option.Name),
			StringComparer.OrdinalIgnoreCase,
			testCase,
			"visible extensions");
		AssertSetEquals(
			testCase.ExpectedCheckedExtensions,
			CollectCheckedExtensionNames(snapshot),
			StringComparer.OrdinalIgnoreCase,
			testCase,
			"checked extensions");

		foreach (var expected in testCase.ExpectedIgnoreOptions)
			AssertIgnoreOption(snapshot, expected, testCase);

		AssertCountContract(snapshot, testCase);
	}

	private static void AssertEquivalentDynamicSections(
		SelectionRefreshSnapshot expected,
		SelectionRefreshSnapshot actual,
		ScopedGoldenCase testCase)
	{
		Assert.Equal(expected.ExtensionOptions, actual.ExtensionOptions);
		Assert.Equal(expected.IgnoreOptions, actual.IgnoreOptions);
		Assert.Equal(expected.ExtensionlessEntriesCount, actual.ExtensionlessEntriesCount);
		Assert.Equal(expected.HasIgnoreOptionCounts, actual.HasIgnoreOptionCounts);
		Assert.Equal(expected.IgnoreOptionCounts, actual.IgnoreOptionCounts);
		Assert.Equal(expected.ControllerImpactCounts, actual.ControllerImpactCounts);
		Assert.Equal(expected.IgnoreOptionStateCache, actual.IgnoreOptionStateCache);
		Assert.Equal(expected.RootAccessDenied, actual.RootAccessDenied);
		Assert.Equal(expected.HadAccessDenied, actual.HadAccessDenied);
		AssertCountContract(actual, testCase);
	}

	private static void AssertIgnoreOption(
		SelectionRefreshSnapshot snapshot,
		ExpectedIgnoreOption expected,
		ScopedGoldenCase testCase)
	{
		var matches = snapshot.IgnoreOptions.Where(option => option.Id == expected.Id).ToArray();
		if (!expected.Visible)
		{
			Assert.Empty(matches);
			return;
		}

		Assert.True(matches.Length == 1, $"{testCase.Name}: expected one visible {expected.Id}, actual={DescribeIgnore(snapshot)}");
		if (expected.Checked.HasValue)
			Assert.Equal(expected.Checked.Value, matches[0].IsChecked);
	}

	private static void AssertCountContract(SelectionRefreshSnapshot snapshot, ScopedGoldenCase testCase)
	{
		if (testCase.ExpectedCounts is not { } counts)
			return;

		if (counts.DotFolders.HasValue)
			Assert.Equal(counts.DotFolders.Value, snapshot.IgnoreOptionCounts.DotFolders);
		if (counts.DotFiles.HasValue)
			Assert.Equal(counts.DotFiles.Value, snapshot.IgnoreOptionCounts.DotFiles);
		if (counts.MinGitIgnoreImpact.HasValue)
			Assert.True(
				snapshot.ControllerImpactCounts.GitIgnore >= counts.MinGitIgnoreImpact.Value,
				$"{testCase.Name}: GitIgnore impact was {snapshot.ControllerImpactCounts.GitIgnore}, expected at least {counts.MinGitIgnoreImpact.Value}.");
		if (counts.MinSmartIgnoreImpact.HasValue)
			Assert.True(
				snapshot.ControllerImpactCounts.SmartIgnore >= counts.MinSmartIgnoreImpact.Value,
				$"{testCase.Name}: SmartIgnore impact was {snapshot.ControllerImpactCounts.SmartIgnore}, expected at least {counts.MinSmartIgnoreImpact.Value}.");
	}

	private static void AssertGoldenTree(
		string rootPath,
		WorkflowServices services,
		SelectionRefreshSnapshot snapshot,
		ScopedGoldenCase testCase)
	{
		var tree = BuildTreeFromSnapshot(rootPath, services, snapshot);
		foreach (var path in testCase.ExpectedVisiblePaths)
			Assert.True(ContainsPath(tree.Root, path), $"{testCase.Name}: expected '{path}' to be visible.");
		foreach (var path in testCase.ExpectedHiddenPaths)
			Assert.False(ContainsPath(tree.Root, path), $"{testCase.Name}: expected '{path}' to be hidden.");
	}

	private static TreeBuildResult BuildTreeFromSnapshot(
		string rootPath,
		WorkflowServices services,
		SelectionRefreshSnapshot snapshot,
		ProjectTreeInventorySnapshot? inventory = null)
	{
		var rules = services.IgnoreRulesService.Build(
			rootPath,
			CollectActiveIgnoreOptionIds(snapshot),
			CollectCheckedRootNames(snapshot));
		var options = new TreeFilterOptions(
			AllowedExtensions: CollectCheckedExtensionNames(snapshot),
			AllowedRootFolders: CollectCheckedRootNames(snapshot),
			IgnoreRules: rules);
		var builder = new TreeBuilder();
		return inventory is null
			? builder.Build(rootPath, options, TestContext.Current.CancellationToken)
			: builder.Build(inventory, options, TestContext.Current.CancellationToken);
	}

	private static HashSet<IgnoreOptionId> CollectActiveIgnoreOptionIds(SelectionRefreshSnapshot snapshot)
	{
		var selected = CollectCheckedIgnoreOptionIds(snapshot);
		foreach (var (optionId, isChecked) in snapshot.IgnoreOptionStateCache)
		{
			if (isChecked)
				selected.Add(optionId);
		}

		return selected;
	}

	private static IReadOnlyList<string> FlattenTree(FileSystemNode root)
	{
		var paths = new List<string>();
		Collect(root, prefix: string.Empty, paths);
		return paths;

		static void Collect(FileSystemNode node, string prefix, List<string> paths)
		{
			var path = string.IsNullOrEmpty(prefix) ? node.Name : $"{prefix}/{node.Name}";
			paths.Add(path);
			foreach (var child in node.Children.OrderBy(static child => child.Name, StringComparer.Ordinal))
				Collect(child, path, paths);
		}
	}

	private static bool ContainsPath(FileSystemNode root, string relativePath)
	{
		var current = root;
		foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
		{
			var next = current.Children.FirstOrDefault(child => string.Equals(child.Name, segment, StringComparison.Ordinal));
			if (next is null)
				return false;

			current = next;
		}

		return true;
	}

	private static TreeNodeDescriptor ToDescriptor(FileSystemNode node) =>
		new(
			DisplayName: node.Name,
			FullPath: node.FullPath,
			IsDirectory: node.IsDirectory,
			IsAccessDenied: node.IsAccessDenied,
			IconKey: node.IsDirectory ? "folder" : "file",
			Children: node.Children.Select(ToDescriptor).ToArray());

	private static string NormalizeExportFragment(string fragment) =>
		fragment.Replace('\\', '/');

	private static void AssertSetEquals(
		IReadOnlySet<string> expected,
		IEnumerable<string> actualValues,
		IEqualityComparer<string> comparer,
		ScopedGoldenCase testCase,
		string label)
	{
		var actual = actualValues.ToHashSet(comparer);
		Assert.True(
			expected.SetEquals(actual),
			$"{testCase.Name}: unexpected {label}. Expected=[{string.Join(", ", expected.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase))}], Actual=[{string.Join(", ", actual.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase))}].");
	}

	private static string DescribeIgnore(SelectionRefreshSnapshot snapshot) =>
		string.Join(", ", snapshot.IgnoreOptions.Select(static option => $"{option.Id}:{option.IsChecked}"));

	private static ScopedGoldenCase CreateCase(
		string name,
		string[]? roots,
		string[]? extensions,
		IReadOnlyDictionary<IgnoreOptionId, bool>? forcedStates,
		string[] expectedVisibleExtensions,
		IReadOnlyCollection<ExpectedIgnoreOption> expectedIgnoreOptions,
		string[] expectedVisiblePaths,
		string[] expectedHiddenPaths,
		ExpectedCountContract? expectedCounts,
		string[]? expectedCheckedExtensions = null,
		string[]? expectedCheckedRoots = null)
	{
		var expectedVisibleSet = Set(expectedVisibleExtensions);
		var selectedExtensions = extensions is null ? null : Set(extensions);
		var checkedExtensions = expectedCheckedExtensions is not null
			? Set(expectedCheckedExtensions)
			: selectedExtensions is null
				? expectedVisibleSet
				: selectedExtensions.Where(expectedVisibleSet.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);

		return new ScopedGoldenCase(
			Name: name,
			SelectedRoots: roots is null ? null : Set(roots, PathComparer.Default),
			SelectedExtensions: selectedExtensions,
			ForcedIgnoreStates: forcedStates,
			ExpectedCheckedRoots: expectedCheckedRoots is not null
				? Set(expectedCheckedRoots, PathComparer.Default)
				: roots is null
					? Set(AllRootNames, PathComparer.Default)
					: Set(roots, PathComparer.Default),
			ExpectedVisibleExtensions: expectedVisibleSet,
			ExpectedCheckedExtensions: checkedExtensions,
			ExpectedIgnoreOptions: expectedIgnoreOptions,
			ExpectedVisiblePaths: expectedVisiblePaths,
			ExpectedHiddenPaths: expectedHiddenPaths,
			ExpectedCounts: expectedCounts);
	}

	private static ExpectedIgnoreOption ExpectedVisible(IgnoreOptionId id, bool isChecked) =>
		new(id, Visible: true, Checked: isChecked);

	private static ExpectedIgnoreOption ExpectedHidden(IgnoreOptionId id) =>
		new(id, Visible: false, Checked: null);

	private static IReadOnlyDictionary<IgnoreOptionId, bool> States(params (IgnoreOptionId Id, bool IsChecked)[] states) =>
		states.ToDictionary(static state => state.Id, static state => state.IsChecked);

	private static IReadOnlyDictionary<IgnoreOptionId, bool> AllOffStates() =>
		Enum.GetValues<IgnoreOptionId>().ToDictionary(static optionId => optionId, static _ => false);

	private static HashSet<string> Set(IEnumerable<string> values) =>
		Set(values, StringComparer.OrdinalIgnoreCase);

	private static HashSet<string> Set(IEnumerable<string> values, IEqualityComparer<string> comparer) =>
		new(values, comparer);

	public sealed record ScopedGoldenCase(
		string Name,
		IReadOnlySet<string>? SelectedRoots,
		IReadOnlySet<string>? SelectedExtensions,
		IReadOnlyDictionary<IgnoreOptionId, bool>? ForcedIgnoreStates,
		IReadOnlySet<string> ExpectedCheckedRoots,
		IReadOnlySet<string> ExpectedVisibleExtensions,
		IReadOnlySet<string> ExpectedCheckedExtensions,
		IReadOnlyCollection<ExpectedIgnoreOption> ExpectedIgnoreOptions,
		IReadOnlyCollection<string> ExpectedVisiblePaths,
		IReadOnlyCollection<string> ExpectedHiddenPaths,
		ExpectedCountContract? ExpectedCounts)
	{
		public override string ToString() => Name;
	}

	public sealed record ExpectedIgnoreOption(IgnoreOptionId Id, bool Visible, bool? Checked);

	public sealed record ExpectedCountContract(
		int? DotFolders,
		int? DotFiles,
		int? MinGitIgnoreImpact,
		int? MinSmartIgnoreImpact);

	public sealed record ExportGoldenCase(
		ScopedGoldenCase Scenario,
		IReadOnlyCollection<string> ExpectedFragments,
		IReadOnlyCollection<string> ForbiddenFragments)
	{
		public override string ToString() => Scenario.Name;
	}
}
