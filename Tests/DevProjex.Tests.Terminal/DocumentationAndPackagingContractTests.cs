using System.CommandLine;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DevProjex.Tests.Terminal;

public sealed class DocumentationAndPackagingContractTests
{
	private static readonly string[] RequiredDocumentation =
	[
		"CLI-V1-Contract.md",
		"CommandLine.md",
		"TerminalWorkspace.md",
		"CLI-Output-Contract.md",
		"CLI-Migration.md",
		"CLI-Architecture.md",
		"CLI-Profiles.md",
		"Desktop-Control.md"
	];

	[Fact]
	public void ReadmeCommandExamplesParseAgainstTheProductionCommandTree()
	{
		var rootPath = FindRepositoryRoot();
		var readme = File.ReadAllText(Path.Combine(rootPath, "README.md"));
		var section = ExtractSection(readme, "## Command Line", "Use it to:");
		var examples = section
			.Split('\n')
			.Select(static line => line.Trim())
			.Where(static line => line.StartsWith("devprojex", StringComparison.Ordinal))
			.ToArray();
		var commandTree = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();

		Assert.InRange(examples.Length, 5, 7);
		foreach (var example in examples)
		{
			var arguments = example
				.Split(' ', StringSplitOptions.RemoveEmptyEntries)
				.Skip(1)
				.ToArray();
			if (arguments.Length == 0)
			{
				var environment = new TestTerminalEnvironment
				{
					HasAttachedConsole = true,
					IsInputInteractive = true,
					IsOutputInteractive = true
				};
				Assert.Equal(
					ProcessInvocationMode.Terminal,
					ProcessInvocationRouter.Resolve(
						arguments,
						environment,
						hasPendingDesktopRequest: false,
						isFrameworkDependentLaunch: false));
				continue;
			}
			var parseResult = commandTree.Parse(arguments);

			Assert.True(
				parseResult.Errors.Count == 0,
				$"README example does not parse: {example}{Environment.NewLine}" +
				string.Join(Environment.NewLine, parseResult.Errors.Select(static error => error.Message)));
		}
	}

	[Fact]
	public void CliDocumentationCoversEveryPublicCommandAndRequiredContractDocument()
	{
		var rootPath = FindRepositoryRoot();
		var docsPath = Path.Combine(rootPath, "Docs");
		foreach (var fileName in RequiredDocumentation)
			Assert.True(File.Exists(Path.Combine(docsPath, fileName)), $"Missing CLI document: {fileName}");

		var commandLine = File.ReadAllText(Path.Combine(docsPath, "CommandLine.md"));
		var commandTree = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		foreach (var path in EnumeratePublicCommandPaths(commandTree))
		{
			var syntax = $"devprojex {string.Join(' ', path)}".TrimEnd();
			Assert.Contains(syntax, commandLine, StringComparison.Ordinal);
		}

		Assert.Contains("Exclusions", commandLine, StringComparison.Ordinal);
		Assert.Contains("--git-mode <none|gitignore|tracked>", commandLine, StringComparison.Ordinal);
		Assert.Contains("stdout", commandLine, StringComparison.Ordinal);
		Assert.Contains("stderr", commandLine, StringComparison.Ordinal);
	}

	[Fact]
	public void ReleaseDocumentationStatesCurrentSurfaceAndArtifactLimits()
	{
		var rootPath = FindRepositoryRoot();
		var v1Contract = File.ReadAllText(
			Path.Combine(rootPath, "Docs", "CLI-V1-Contract.md"));
		var commandLine = File.ReadAllText(
			Path.Combine(rootPath, "Docs", "CommandLine.md"));
		var contributing = File.ReadAllText(
			Path.Combine(rootPath, "CONTRIBUTING.md"));
		var readme = File.ReadAllText(
			Path.Combine(rootPath, "README.md"));
		var macPackaging = File.ReadAllText(
			Path.Combine(rootPath, "Packaging", "MacOS", "README.md"));
		var normalizedCommandLine = commandLine.ReplaceLineEndings(" ");
		var normalizedContributing = contributing.ReplaceLineEndings(" ");

		Assert.DoesNotContain(
			"All three surfaces consume the same",
			v1Contract,
			StringComparison.Ordinal);
		Assert.Contains(
			"interchangeable implementation pipeline",
			normalizedCommandLine,
			StringComparison.Ordinal);
		Assert.Contains(
			"interchangeable implementation pipeline",
			normalizedContributing,
			StringComparison.Ordinal);
		Assert.Contains(
			"`DevProjex.exe` on Windows and `DevProjex` on Linux and macOS",
			commandLine,
			StringComparison.Ordinal);
		Assert.Contains(
			"portable-profile destinations are accepted only outside it",
			readme,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"DevProjex never modifies your files",
			readme,
			StringComparison.Ordinal);
		Assert.Contains(
			"<string>14.0</string>",
			macPackaging,
			StringComparison.Ordinal);
		Assert.Contains(
			"unprepared `.app`",
			macPackaging,
			StringComparison.Ordinal);
		Assert.Contains(
			"do not build, sign, notarize, or execute this bundle",
			macPackaging,
			StringComparison.Ordinal);
	}

	[Fact]
	public void UserDocumentationDoesNotAdvertiseTheRemovedFlatCli()
	{
		var rootPath = FindRepositoryRoot();
		var files = new[]
			{
				Path.Combine(rootPath, "README.md"),
				Path.Combine(rootPath, "Docs", "CommandLine.md")
			}
			.Concat(Directory.EnumerateFiles(Path.Combine(rootPath, "Assets", "HelpContent"), "help.*.txt"))
			.ToArray();
		string[] removedSyntax =
		[
			"--no-ui",
			"--silent",
			"--report ",
			"--copy ",
			"--benchmark-ui",
			"--session-metrics",
			"--export tree",
			"--export content",
			"--export tree-content"
		];

		foreach (var file in files)
		{
			var content = File.ReadAllText(file);
			foreach (var token in removedSyntax)
				Assert.DoesNotContain(token, content, StringComparison.Ordinal);
		}
	}

	[Theory]
	[InlineData("help.en.txt")]
	[InlineData("help.ru.txt")]
	[InlineData("help.de.txt")]
	[InlineData("help.fr.txt")]
	[InlineData("help.it.txt")]
	[InlineData("help.es.txt")]
	[InlineData("help.pt.txt")]
	[InlineData("help.pt-pt.txt")]
	[InlineData("help.kk.txt")]
	[InlineData("help.tg.txt")]
	[InlineData("help.uz.txt")]
	public void BuiltInHelpDocumentsTheTerminalWorkspaceAndDirectCommands(string fileName)
	{
		var path = Path.Combine(FindRepositoryRoot(), "Assets", "HelpContent", fileName);
		var content = File.ReadAllText(path);

		Assert.Contains("`devprojex`", content, StringComparison.Ordinal);
		Assert.Contains("`devprojex open . --preview`", content, StringComparison.Ordinal);
		Assert.Contains("`devprojex analyze . --format json`", content, StringComparison.Ordinal);
		Assert.Contains("`devprojex export context . --format markdown -o ../devprojex-context.md`", content, StringComparison.Ordinal);
		Assert.Contains("`devprojex export project . --as folder -o ../devprojex-submission`", content, StringComparison.Ordinal);
		Assert.Contains("`devprojex export project . --as zip -o ../devprojex-submission.zip`", content, StringComparison.Ordinal);
		Assert.Contains("`--git-mode`", content, StringComparison.Ordinal);
		Assert.Contains("`--exclude`", content, StringComparison.Ordinal);
	}

	[Fact]
	public void PackagingKeepsTerminalAsALibraryInsideTheSingleDesktopExecutable()
	{
		var rootPath = FindRepositoryRoot();
		var terminalProjectPath = Path.Combine(
			rootPath,
			"Apps",
			"Terminal",
			"DevProjex.Terminal.csproj");
		var desktopProjectPath = Path.Combine(
			rootPath,
			"Apps",
			"Avalonia",
			"DevProjex.Avalonia.csproj");
		var terminalProject = XDocument.Load(terminalProjectPath);
		var desktopProject = XDocument.Load(desktopProjectPath);

		Assert.DoesNotContain(
			terminalProject.Descendants("OutputType"),
			static element => element.Value.Equals("Exe", StringComparison.OrdinalIgnoreCase));
		var referencedProjects = desktopProject
			.Descendants("ProjectReference")
			.Select(element => Path.GetFullPath(
				Path.Combine(
					Path.GetDirectoryName(desktopProjectPath)!,
					(element.Attribute("Include")?.Value ?? string.Empty)
					.Replace('\\', Path.DirectorySeparatorChar)
					.Replace('/', Path.DirectorySeparatorChar))))
			.ToArray();
		Assert.Contains(
			Path.GetFullPath(terminalProjectPath),
			referencedProjects,
			StringComparer.OrdinalIgnoreCase);

		var workflow = File
			.ReadAllText(Path.Combine(rootPath, ".github", "workflows", "release-validate.yml"))
			.ReplaceLineEndings("\n");
		foreach (var rid in new[] { "win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64" })
			Assert.Contains(rid, workflow, StringComparison.Ordinal);
		Assert.Contains(
			"runner = 'macos-15'; rid = 'osx-arm64'",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_TERMINAL_HOST=1", workflow, StringComparison.Ordinal);
		Assert.Contains("Validate Single-File Output", workflow, StringComparison.Ordinal);
		Assert.Contains(
			"Get-ChildItem -LiteralPath $publishDirectory -File -Recurse",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains("$files.Count -ne 1", workflow, StringComparison.Ordinal);
		Assert.Contains(
			"[System.IO.Path]::GetRelativePath($publishDirectory, $_.FullName)",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains("${{ matrix.binary }}", workflow, StringComparison.Ordinal);
		Assert.Contains("Desktop IPC and Redirected EOF Smoke", workflow, StringComparison.Ordinal);
		Assert.Contains("retained redirected CLI handles", workflow, StringComparison.Ordinal);
		Assert.Contains("[int] $TimeoutMilliseconds = 30000", workflow, StringComparison.Ordinal);
		Assert.Contains("-TimeoutMilliseconds 150000", workflow, StringComparison.Ordinal);
		Assert.Contains("Get-DesktopTimeoutDiagnostics", workflow, StringComparison.Ordinal);
		Assert.Contains(
			"[IO.Path]::GetFullPath([string]$_.projectPath)",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"$statusResponse = $status.Stdout | ConvertFrom-Json",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains("$statusResponse.ok", workflow, StringComparison.Ordinal);
		Assert.Contains("$state = $statusResponse.state", workflow, StringComparison.Ordinal);
		Assert.DoesNotContain(
			"$state = $status.Stdout | ConvertFrom-Json",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains("env -u CI \"$2\"", workflow, StringComparison.Ordinal);
		Assert.Contains("Portable Launcher ConPTY TUI Smoke", workflow, StringComparison.Ordinal);
		Assert.Contains("Published Native PTY TUI Smoke", workflow, StringComparison.Ordinal);
		Assert.Contains("Published Single-File Extraction Contract", workflow, StringComparison.Ordinal);
		Assert.Contains(
			"PublishedSingleFileExtractionProcessTests",
			workflow,
			StringComparison.Ordinal);
		var publishStepIndex = workflow.IndexOf(
			"\n      - name: Publish\n",
			StringComparison.Ordinal);
		var completionStepName =
			"\n      - name: Published Completion Native Shell Integration\n";
		var completionStepIndex = workflow.IndexOf(
			completionStepName,
			StringComparison.Ordinal);
		Assert.True(
			publishStepIndex >= 0,
			"The release workflow must publish the application before validating it.");
		Assert.True(
			completionStepIndex > publishStepIndex,
			"The native-shell completion gate must run after the application is published.");
		var completionStepEndIndex = workflow.IndexOf(
			"\n      - name:",
			completionStepIndex + completionStepName.Length,
			StringComparison.Ordinal);
		var completionStep = workflow[
			completionStepIndex..
			(completionStepEndIndex >= 0 ? completionStepEndIndex : workflow.Length)];
		Assert.Contains(
			"artifacts/publish/${{ matrix.rid }}/${{ matrix.binary }}",
			completionStep,
			StringComparison.Ordinal);
		Assert.Contains(
			"FullyQualifiedName~GeneratedCompletionNativeShellIntegrationTests",
			completionStep,
			StringComparison.Ordinal);
		Assert.Contains(
			"DEVPROJEX_REQUIRED_COMPLETION_SHELLS",
			completionStep,
			StringComparison.Ordinal);
		Assert.Contains(
			"apt-get install -y xvfb zsh fish",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"Nested-Mount Destination Safety Gate (Linux x64)",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"Enable Rootless Mount Namespace Gate (Linux x64)",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"apparmor_restrict_unprivileged_userns",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"unshare -Urnm true",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"Restore Rootless Mount Namespace Policy (Linux x64)",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"always() && matrix.rid == 'linux-x64'",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"DEVPROJEX_REQUIRE_ROOTLESS_MOUNT_TESTS",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"DestinationNestedMountProcessTests.AnalyzeRejectsAliasToFileSystemMountedInsideSource",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"DestinationNestedMountProcessTests.ContextForceRejectsFileBindAliasToMountedSourceFile",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains("project folder export", workflow, StringComparison.Ordinal);
		Assert.Contains("project ZIP export", workflow, StringComparison.Ordinal);
		Assert.Contains(
			"[System.IO.Compression.ZipFile]::OpenRead",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains("Published Broken Pipe Smoke", workflow, StringComparison.Ordinal);
		Assert.Contains("Startup Smoke (macOS)", workflow, StringComparison.Ordinal);
		Assert.Contains("mixed-case analysis JSON", workflow, StringComparison.Ordinal);
		Assert.Contains("NO_COLOR analysis", workflow, StringComparison.Ordinal);
		Assert.Contains("context dry-run", workflow, StringComparison.Ordinal);
		var dryRunSectionStart = workflow.IndexOf(
			"$dryRunDirectory =",
			StringComparison.Ordinal);
		Assert.True(
			dryRunSectionStart >= 0,
			"The release workflow must define a context dry-run destination.");
		var dryRunSectionEnd = workflow.IndexOf(
			"$conflictDestination =",
			dryRunSectionStart,
			StringComparison.Ordinal);
		Assert.True(
			dryRunSectionEnd > dryRunSectionStart,
			"The release workflow must contain a bounded context dry-run smoke.");
		var dryRunSection = workflow[dryRunSectionStart..dryRunSectionEnd];
		var dryRunParentCreationIndex = dryRunSection.IndexOf(
			"New-Item -ItemType Directory -Path $dryRunDirectory -Force",
			StringComparison.Ordinal);
		var dryRunInvocationIndex = dryRunSection.IndexOf(
			"Invoke-NativeCommand -Name \"context dry-run\"",
			StringComparison.Ordinal);
		Assert.True(
			dryRunParentCreationIndex >= 0 &&
			dryRunInvocationIndex > dryRunParentCreationIndex,
			"The dry-run destination parent must exist before preflight.");
		Assert.Contains(
			"Test-Path -LiteralPath $dryRunDirectory -PathType Container",
			dryRunSection,
			StringComparison.Ordinal);
		Assert.Contains(
			"Test-Path -LiteralPath $dryRunDestination",
			dryRunSection,
			StringComparison.Ordinal);
		Assert.Contains(
			"Get-ChildItem -LiteralPath $dryRunDirectory -Force",
			dryRunSection,
			StringComparison.Ordinal);
		Assert.Contains("existing destination conflict", workflow, StringComparison.Ordinal);
		Assert.Contains("real CLI usage-error contract", workflow, StringComparison.Ordinal);
		Assert.Contains(
			"InstallOrRepair_ReleaseGateEnvironment_InstallsOfficialWindowsLauncherThroughPublicApi",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"DEVPROJEX_RELEASE_WINDOWS_LAUNCHER_TARGET",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"DEVPROJEX_RELEASE_WINDOWS_LAUNCHER_LOCAL_APP_DATA",
			workflow,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"Join-Path $env:RUNNER_TEMP \"devprojex.cmd\"",
			workflow,
			StringComparison.Ordinal);
		Assert.DoesNotContain("rem target-base64:", workflow, StringComparison.Ordinal);
		Assert.DoesNotContain(
			"Set-Content -LiteralPath $launcherPath",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"TerminalRecentRepositoriesPtyTests.PopulatedCachedRepositoryOpensOfflineWithCleanIdentity",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"TerminalClonePtyTests.LocalRepositoryCloneThroughApplicationBinaryOpensWorkspaceAndPreservesSource",
			workflow,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"TerminalClonePtyTests.CloneProgressCancellationCleansCacheAndRetryOpensWorkspace",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"TerminalLargePreviewPtyTests.FileBackedPreviewReachesFirstMiddleAndFinalSectionsWithDistinctScrollbars",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"TerminalPtyLifecycleTests.SupportedTerminalSizeMatrixRemainsKeyboardUsableAndWithinViewport",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_TUI_TEST_BINARY", workflow, StringComparison.Ordinal);
		Assert.DoesNotContain("DevProjex.Cli", workflow, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("\"--path\"", workflow, StringComparison.Ordinal);
		Assert.DoesNotContain("\"--copy\"", workflow, StringComparison.Ordinal);
		Assert.DoesNotContain("\"--report\"", workflow, StringComparison.Ordinal);

		var desktopEntry = File.ReadAllText(
			Path.Combine(rootPath, "Packaging", "Linux", "devprojex.desktop"));
		Assert.Contains("Exec=devprojex open %f", desktopEntry, StringComparison.Ordinal);
		Assert.DoesNotContain("Exec=devprojex %F", desktopEntry, StringComparison.Ordinal);

		var macPackaging = File.ReadAllText(
			Path.Combine(rootPath, "Packaging", "MacOS", "README.md"));
		Assert.Contains("DEVPROJEX_TERMINAL_HOST=1", macPackaging, StringComparison.Ordinal);

		var symbolTarget = desktopProject
			.Descendants("Target")
			.Single(element =>
				element.Attribute("Name")?.Value == "RemovePackageSymbolsFromSingleFilePublish");
		Assert.Contains("PublishSingleFile", symbolTarget.Attribute("Condition")?.Value, StringComparison.Ordinal);
		Assert.Contains("DebugSymbols", symbolTarget.Attribute("Condition")?.Value, StringComparison.Ordinal);
		Assert.Contains(
			symbolTarget.Descendants("ResolvedFileToPublish"),
			element =>
				element.Attribute("Remove")?.Value == "@(ResolvedFileToPublish)" &&
				element.Attribute("Condition")?.Value.Contains(".pdb", StringComparison.Ordinal) == true);
	}

	[Fact]
	public void NativePtyAutomationDependencyIsPinnedAndTestOnly()
	{
		var rootPath = FindRepositoryRoot();
		var packageVersions = XDocument.Load(Path.Combine(rootPath, "Directory.Packages.props"));
		var hex1bVersion = packageVersions
			.Descendants("PackageVersion")
			.Single(element => element.Attribute("Include")?.Value == "Hex1b")
			.Attribute("Version")?.Value;
		Assert.Equal("0.165.0", hex1bVersion);
		Assert.DoesNotContain(
			packageVersions.Descendants("PackageVersion"),
			element => element.Attribute("Include")?.Value == "Porta.Pty");

		var terminalTestProject = XDocument.Load(
			Path.Combine(
				rootPath,
				"Tests",
				"DevProjex.Tests.Terminal",
				"DevProjex.Tests.Terminal.csproj"));
		Assert.Contains(
			terminalTestProject.Descendants("PackageReference"),
			element => element.Attribute("Include")?.Value == "Hex1b");

		var productProjectPaths = new[]
		{
			Path.Combine(
				rootPath,
				"Apps",
				"Terminal",
				"DevProjex.Terminal.csproj"),
			Path.Combine(
				rootPath,
				"Apps",
				"Avalonia",
				"DevProjex.Avalonia.csproj")
		};
		foreach (var productProjectPath in productProjectPaths)
		{
			var productProject = XDocument.Load(productProjectPath);
			Assert.DoesNotContain(
				productProject.Descendants("PackageReference"),
				element => element.Attribute("Include")?.Value is "Hex1b" or "Porta.Pty");
		}
	}

	[Fact]
	public void ProductSourcesContainNoEnvironmentDrivenProgressCheckpointBackdoor()
	{
		var rootPath = FindRepositoryRoot();
		var productRoots = new[]
		{
			Path.Combine(rootPath, "Kernel"),
			Path.Combine(rootPath, "Application"),
			Path.Combine(rootPath, "Infrastructure"),
			Path.Combine(rootPath, "Apps")
		};

		foreach (var file in productRoots.SelectMany(path =>
			         Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)))
		{
			var source = File.ReadAllText(file);
			Assert.DoesNotContain(
				"DEVPROJEX_INTERNAL_TUI_PROGRESS_",
				source,
				StringComparison.Ordinal);
			Assert.DoesNotContain(
				"TerminalProgressTestCheckpoint",
				source,
				StringComparison.Ordinal);
		}
	}

	[Fact]
	public void TestSourcesDoNotReferencePreFlattenedApplicationDirectories()
	{
		var rootPath = FindRepositoryRoot();
		var testsRoot = Path.Combine(rootPath, "Tests");
		var obsoletePathPatterns = new[]
		{
			new Regex(
				"\"Apps\"\\s*,\\s*\"Avalonia\"\\s*,\\s*\"DevProjex\\.Avalonia\"",
				RegexOptions.CultureInvariant),
			new Regex(
				"\"Apps\"\\s*,\\s*\"Terminal\"\\s*,\\s*\"DevProjex\\.Terminal\"",
				RegexOptions.CultureInvariant)
		};

		foreach (var sourcePath in Directory.EnumerateFiles(
			         testsRoot,
			         "*.cs",
			         SearchOption.AllDirectories))
		{
			var source = File.ReadAllText(sourcePath);
			foreach (var obsoletePathPattern in obsoletePathPatterns)
			{
				Assert.False(
					obsoletePathPattern.IsMatch(source),
					$"Obsolete pre-flattening path in {Path.GetRelativePath(rootPath, sourcePath)}.");
			}
		}
	}

	private static IEnumerable<IReadOnlyList<string>> EnumeratePublicCommandPaths(RootCommand root)
	{
		var stack = new Stack<(Command Command, string[] Path)>();
		foreach (var command in root.Subcommands.Reverse())
		{
			if (!command.Hidden)
				stack.Push((command, [command.Name]));
		}

		while (stack.Count > 0)
		{
			var (command, path) = stack.Pop();
			yield return path;
			foreach (var child in command.Subcommands.Reverse())
			{
				if (!child.Hidden)
					stack.Push((child, [.. path, child.Name]));
			}
		}
	}

	private static string ExtractSection(string content, string startMarker, string endMarker)
	{
		var start = content.IndexOf(startMarker, StringComparison.Ordinal);
		var end = content.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
		Assert.True(start >= 0, $"Section start not found: {startMarker}");
		Assert.True(end > start, $"Section end not found: {endMarker}");
		return content[start..end];
	}

	private static string FindRepositoryRoot()
		=> PublishedApplicationLocator.FindRepositoryRoot();
}
