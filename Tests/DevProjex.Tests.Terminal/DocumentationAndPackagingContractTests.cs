using System.CommandLine;
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
		Assert.Contains("`devprojex export context . --format markdown -o context.md`", content, StringComparison.Ordinal);
		Assert.Contains("`devprojex export project . --as folder -o submission`", content, StringComparison.Ordinal);
		Assert.Contains("`devprojex export project . --as zip -o submission.zip`", content, StringComparison.Ordinal);
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
			"DevProjex.Terminal",
			"DevProjex.Terminal.csproj");
		var desktopProjectPath = Path.Combine(
			rootPath,
			"Apps",
			"Avalonia",
			"DevProjex.Avalonia",
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

		var workflow = File.ReadAllText(Path.Combine(rootPath, ".github", "workflows", "release-validate.yml"));
		foreach (var rid in new[] { "win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64" })
			Assert.Contains(rid, workflow, StringComparison.Ordinal);
		Assert.Contains(
			"runner = 'macos-15'; rid = 'osx-arm64'",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_TERMINAL_HOST=1", workflow, StringComparison.Ordinal);
		Assert.Contains("Validate Single-File Output", workflow, StringComparison.Ordinal);
		Assert.Contains("$files.Count -ne 1", workflow, StringComparison.Ordinal);
		Assert.Contains("${{ matrix.binary }}", workflow, StringComparison.Ordinal);
		Assert.Contains("Desktop IPC and Redirected EOF Smoke", workflow, StringComparison.Ordinal);
		Assert.Contains("retained redirected CLI handles", workflow, StringComparison.Ordinal);
		Assert.Contains("env -u CI \"$2\"", workflow, StringComparison.Ordinal);
		Assert.Contains("Portable Launcher ConPTY TUI Smoke", workflow, StringComparison.Ordinal);
		Assert.Contains("Published Native PTY TUI Smoke", workflow, StringComparison.Ordinal);
		Assert.Contains(
			"TerminalRecentRepositoriesPtyTests.PopulatedCachedRepositoryOpensOfflineWithCleanIdentity",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains(
			"TerminalLargePreviewPtyTests.FileBackedReadablePreviewReachesFirstMiddleAndFinalSections",
			workflow,
			StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_TUI_TEST_BINARY", workflow, StringComparison.Ordinal);
		Assert.DoesNotContain("DevProjex.Cli", workflow, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("\"--path\"", workflow, StringComparison.Ordinal);
		Assert.DoesNotContain("\"--copy\"", workflow, StringComparison.Ordinal);
		Assert.DoesNotContain("\"--report\"", workflow, StringComparison.Ordinal);

		var desktopEntry = File.ReadAllText(
			Path.Combine(rootPath, "Packaging", "Linux", "devprojex.desktop"));
		Assert.Contains("Exec=devprojex open %F", desktopEntry, StringComparison.Ordinal);
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
