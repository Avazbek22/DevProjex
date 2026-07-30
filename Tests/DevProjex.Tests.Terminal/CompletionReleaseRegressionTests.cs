using System.Globalization;
using System.CommandLine;

namespace DevProjex.Tests.Terminal;

public sealed class CompletionReleaseRegressionTests
{
	[Fact]
	public async Task RootCompletionContainsOnlyPublicRootCommands()
	{
		var candidates = await CompleteAsync("devprojex ");

		Assert.Contains("analyze", candidates);
		Assert.Contains("export", candidates);
		Assert.Contains("tui", candidates);
		Assert.DoesNotContain("dev", candidates);
		Assert.DoesNotContain("benchmark", candidates);
	}

	[Fact]
	public async Task AnalyzeCompletionIsScopedToAnalyzeAndRecursiveOptions()
	{
		var candidates = await CompleteAsync("devprojex analyze . --");

		Assert.Contains("--format", candidates);
		Assert.Contains("--git-mode", candidates);
		Assert.Contains("--language", candidates);
		Assert.DoesNotContain("--as", candidates);
		Assert.DoesNotContain("reset", candidates);
	}

	[Fact]
	public async Task OutputKindCompletionContainsOnlyCanonicalValues()
	{
		var candidates = await CompleteAsync("devprojex export project . --as ");

		Assert.Equal(["folder", "zip"], candidates.Order(StringComparer.Ordinal));
	}

	[Fact]
	public async Task UsedNonRepeatableOptionIsNotSuggestedAgain()
	{
		var candidates = await CompleteAsync(
			"devprojex analyze . --format json --");

		Assert.DoesNotContain("--format", candidates);
		Assert.Contains("--root", candidates);
	}

	[Fact]
	public async Task RepeatableOptionRemainsAvailable()
	{
		var candidates = await CompleteAsync(
			"devprojex analyze . --root src --");

		Assert.Contains("--root", candidates);
	}

	[Fact]
	public async Task EmptyCommandLineCompletesPublicRootWithoutLeakingDev()
	{
		var candidates = await CompleteAsync(string.Empty);

		Assert.Contains("analyze", candidates);
		Assert.DoesNotContain("dev", candidates);
	}

	[Fact]
	public async Task AutoProfileIsScopedToInteractiveEntryPoints()
	{
		var direct = await CompleteAsync("devprojex analyze . --profile ");
		var open = await CompleteAsync("devprojex open . --profile ");
		var tui = await CompleteAsync("devprojex tui . --profile ");

		Assert.DoesNotContain("auto", direct);
		Assert.Contains("standard", direct);
		Assert.Contains("local", direct);
		Assert.Contains("auto", open);
		Assert.Contains("auto", tui);
	}

	[Fact]
	public async Task CompletionPreservesOptionNameForEqualsForm()
	{
		var candidates = await CompleteAsync("devprojex analyze . --format=j");

		Assert.Equal(["--format=json"], candidates);
	}

	[Theory]
	[InlineData(
		"devprojex analyze . --profile=st",
		"--profile=standard")]
	[InlineData(
		"devprojex tui . --profile=a",
		"--profile=auto")]
	public async Task ProfileSpecialValuesSupportEqualsCompletion(
		string line,
		string expected)
	{
		var candidates = await CompleteAsync(line);

		Assert.Contains(expected, candidates);
	}

	[Fact]
	public async Task ProfileFileSupportsEqualsCompletion()
	{
		using var workspace = new TemporaryDirectory();
		var profile = workspace.WriteFile("Профиль с пробелом.json", "{}");
		var prefix = Path.Combine(workspace.Path, "Проф");
		var line = $"devprojex analyze . --profile={prefix}";

		var candidates = await CompleteAsync(line);

		Assert.Contains($"--profile={profile}", candidates);
	}

	[Fact]
	public async Task CompletedChoiceValueReturnsToCommandOptionContext()
	{
		var candidates = await CompleteAsync(
			"devprojex analyze . --format json ");

		Assert.Contains("--root", candidates);
		Assert.DoesNotContain("json", candidates);
		Assert.DoesNotContain("text", candidates);
		Assert.DoesNotContain(
			".git" + Path.DirectorySeparatorChar,
			candidates);
	}

	[Theory]
	[InlineData("devprojex analyze . --format=j")]
	[InlineData("devprojex analyze . --plain --color ")]
	public void PartialChoiceCompletionNeverThrows(string line)
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();

		var exception = Record.Exception(
			() => ContextAwareCompletionEngine.Complete(root, line, line.Length));

		Assert.Null(exception);
	}

	[Fact]
	public async Task DirectoryAndProfileFileCompletionPreserveUnicodeAndSpaces()
	{
		using var workspace = new TemporaryDirectory();
		var directory = workspace.CreateDirectory("Проект с пробелом");
		var profile = workspace.WriteFile("профиль с пробелом.json", "{}");
		var directoryPrefix = Path.Combine(workspace.Path, "Про");
		var profilePrefix = Path.Combine(workspace.Path, "проф");

		var projectCandidates = await CompleteAsync(
			$"devprojex analyze {directoryPrefix}");
		var profileCandidates = await CompleteAsync(
			$"devprojex analyze . --profile {profilePrefix}");

		Assert.Contains(directory + Path.DirectorySeparatorChar, projectCandidates);
		Assert.Contains(profile, profileCandidates);
	}

	[Fact]
	public async Task DelimiterNeverReintroducesOptionsAsArgumentCandidates()
	{
		var candidates = await CompleteAsync("devprojex analyze -- --co");

		Assert.DoesNotContain("--color", candidates);
		Assert.DoesNotContain("--copy", candidates);
	}

	[Fact]
	public async Task DelimiterDoesNotLeakSlashHelpAliases()
	{
		var candidates = await CompleteAsync("devprojex analyze -- ");

		Assert.DoesNotContain("/?", candidates);
		Assert.DoesNotContain("/h", candidates);
	}

	[Fact]
	public void DelimiterPreservesLeadingDashArgumentCandidates()
	{
		var root = new RootCommand();
		var inspect = new Command("inspect");
		var project = new Argument<string>("PROJECT")
		{
			CompletionSources =
			{
				_ => ["--leading-project"]
			}
		};
		inspect.Arguments.Add(project);
		root.Subcommands.Add(inspect);
		const string line = "devprojex inspect -- --lea";

		var candidates = ContextAwareCompletionEngine.Complete(
			root,
			line,
			line.Length);

		Assert.Contains("--leading-project", candidates);
	}

	[Fact]
	public async Task CompletionSuppressesParserInvalidOptionCombinations()
	{
		var afterLast = await CompleteAsync("devprojex open --last --");
		var afterFolder = await CompleteAsync(
			"devprojex export project . --as folder -o ../result --");
		var afterZip = await CompleteAsync(
			"devprojex export project . --as zip -o ../result.zip --");
		var contextStdout = await CompleteAsync("devprojex export context . --");
		var contextFile = await CompleteAsync(
			"devprojex export context . -o ../context.md --");

		Assert.DoesNotContain("--profile", afterLast);
		Assert.DoesNotContain("--root", afterLast);
		Assert.DoesNotContain("--select", afterLast);
		Assert.DoesNotContain("--force", afterFolder);
		Assert.Contains("--force", afterZip);
		Assert.DoesNotContain("--force", contextStdout);
		Assert.Contains("--force", contextFile);
	}

	[Fact]
	public async Task PlainAndAlwaysColorCompleteOnlyCompatibleValues()
	{
		var colorValues = await CompleteAsync(
			"devprojex analyze . --plain --color ");
		var options = await CompleteAsync(
			"devprojex analyze . --color always --");

		Assert.Equal(["auto", "never"], colorValues.Order(StringComparer.Ordinal));
		Assert.DoesNotContain("--plain", options);
	}

	[Theory]
	[InlineData("bash", "COMP_LINE")]
	[InlineData("zsh", "BUFFER")]
	[InlineData("fish", "commandline")]
	[InlineData("powershell", "$cursorPosition")]
	public async Task GeneratedScriptDelegatesCursorContextToTheProductionTree(
		string shell,
		string cursorMarker)
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			["completion", shell],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Contains("dev complete", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains(cursorMarker, environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("\u001b", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task EncodedCompletionTransportPreservesQuotedUnicodeAndWhitespace()
	{
		const string line =
			"devprojex analyze \"C:\\Program Files\\Проект O'Brien\" --format ";
		var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(line));
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			[
				"dev",
				"complete",
				"--position",
				line.Length.ToString(CultureInfo.InvariantCulture),
				"--base64",
				encoded
			],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardError);
		Assert.Equal(
			["json", "text"],
			environment.StandardOutput
				.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
				.Order(StringComparer.Ordinal));
	}

	[Fact]
	public async Task InvalidEncodedCompletionTransportFailsWithoutCandidates()
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			["dev", "complete", "--position", "1", "--base64", "not-base64!"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains(
			"error[DPX-CLI-INVALID-SYNTAX]",
			environment.StandardError,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task EncodedCompletionWorkingDirectoryControlsRelativePathCandidates()
	{
		using var workspace = new TemporaryDirectory();
		var completionDirectory = workspace.CreateDirectory("completion cwd");
		var project = Directory.CreateDirectory(
			Path.Combine(completionDirectory, "Проект O'Brien & $draft")).FullName;
		var line =
			$"devprojex analyze \".{Path.DirectorySeparatorChar}Проект O";
		var encodedLine = Convert.ToBase64String(Encoding.UTF8.GetBytes(line));
		var encodedWorkingDirectory = Convert.ToBase64String(
			Encoding.UTF8.GetBytes(completionDirectory));
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			[
				"dev",
				"complete",
				"--position",
				line.Length.ToString(CultureInfo.InvariantCulture),
				"--base64",
				"--working-directory-base64",
				encodedWorkingDirectory,
				encodedLine
			],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardError);
		Assert.Contains(
			$".{Path.DirectorySeparatorChar}{Path.GetFileName(project)}{Path.DirectorySeparatorChar}",
			environment.StandardOutput.Split(
				Environment.NewLine,
				StringSplitOptions.RemoveEmptyEntries));
		Assert.DoesNotContain(
			environment.StandardOutput.Split(
				Environment.NewLine,
				StringSplitOptions.RemoveEmptyEntries),
			candidate =>
				candidate.Equals(
					$"Apps{Path.DirectorySeparatorChar}",
					StringComparison.Ordinal) ||
				candidate.Equals(
					$"Application{Path.DirectorySeparatorChar}",
					StringComparison.Ordinal));
	}

	[Fact]
	public void ExplicitCompletionWorkingDirectoryAppliesToOptionAndProjectRelativeSources()
	{
		using var workspace = new TemporaryDirectory();
		var completionDirectory = workspace.CreateDirectory("completion cwd");
		var outputDirectory = Directory.CreateDirectory(
			Path.Combine(completionDirectory, "reports")).FullName;
		var projectDirectory = Directory.CreateDirectory(
			Path.Combine(completionDirectory, "project")).FullName;
		var selectedDirectory = Directory.CreateDirectory(
			Path.Combine(projectDirectory, "src")).FullName;
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		var separator = Path.DirectorySeparatorChar;

		var outputCandidates = ContextAwareCompletionEngine.Complete(
			root,
			$"devprojex analyze . -o .{separator}rep",
			$"devprojex analyze . -o .{separator}rep".Length,
			completionDirectory);
		var selectedCandidates = ContextAwareCompletionEngine.Complete(
			root,
			$"devprojex analyze .{separator}project --select s",
			$"devprojex analyze .{separator}project --select s".Length,
			completionDirectory);

		Assert.Equal(
			[$".{separator}{Path.GetFileName(outputDirectory)}{separator}"],
			outputCandidates);
		Assert.Equal(
			[$"{Path.GetFileName(selectedDirectory)}{separator}"],
			selectedCandidates);
	}

	[Fact]
	public async Task CompletionBaseDirectoryScopesAreNestedAndConcurrentFlowSafe()
	{
		using var workspace = new TemporaryDirectory();
		var firstBase = workspace.CreateDirectory("first");
		var secondBase = workspace.CreateDirectory("second");
		Directory.CreateDirectory(Path.Combine(firstBase, "first-only"));
		Directory.CreateDirectory(Path.Combine(secondBase, "second-only"));
		var separator = Path.DirectorySeparatorChar;

		using (FileSystemCompletionSource.UseBaseDirectory(firstBase))
		{
			Assert.Equal(
				[$"first-only{separator}"],
				FileSystemCompletionSource.Complete(
					string.Empty,
					FileSystemCompletionKind.Directories));
			using (FileSystemCompletionSource.UseBaseDirectory(secondBase))
			{
				Assert.Equal(
					[$"second-only{separator}"],
					FileSystemCompletionSource.Complete(
						string.Empty,
						FileSystemCompletionKind.Directories));
			}
			Assert.Equal(
				[$"first-only{separator}"],
				FileSystemCompletionSource.Complete(
					string.Empty,
					FileSystemCompletionKind.Directories));
		}

		var cancellationToken = TestContext.Current.CancellationToken;
		using var barrier = new Barrier(participantCount: 2);
		var first = CompleteConcurrentlyAsync(firstBase);
		var second = CompleteConcurrentlyAsync(secondBase);
		var results = await Task.WhenAll(first, second);

		Assert.Equal([$"first-only{separator}"], results[0]);
		Assert.Equal([$"second-only{separator}"], results[1]);

		Task<string[]> CompleteConcurrentlyAsync(string baseDirectory) =>
			Task.Run(
				() =>
				{
					using var scope =
						FileSystemCompletionSource.UseBaseDirectory(baseDirectory);
					Assert.True(
						barrier.SignalAndWait(
							TimeSpan.FromSeconds(10),
							cancellationToken),
						"The completion scopes were not active concurrently.");
					return FileSystemCompletionSource
						.Complete(
							string.Empty,
							FileSystemCompletionKind.Directories)
						.ToArray();
				},
				cancellationToken);
	}

	[Fact]
	public async Task InvalidEncodedCompletionWorkingDirectoryFailsWithoutCandidates()
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			[
				"dev",
				"complete",
				"--position",
				"1",
				"--working-directory-base64",
				"not-base64!",
				"x"
			],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains(
			"error[DPX-CLI-INVALID-SYNTAX]",
			environment.StandardError,
			StringComparison.Ordinal);
	}

	[Fact]
	public void IncompleteQuotedProjectPathUsesTheCompleteLexicalPrefix()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("Проект O'Brien & draft");
		var prefix = Path.Combine(workspace.Path, "Проект O");
		var line = $"devprojex analyze \"{prefix}";
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();

		var candidates = ContextAwareCompletionEngine.Complete(
			root,
			line,
			line.Length);

		Assert.Contains(project + Path.DirectorySeparatorChar, candidates);
	}

	private static async Task<IReadOnlyList<string>> CompleteAsync(string line)
	{
		var environment = new TestTerminalEnvironment();
		var exitCode = await new TerminalApplication(environment).RunAsync(
			[
				"dev",
				"complete",
				"--position",
				line.Length.ToString(CultureInfo.InvariantCulture),
				line
			],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardError);
		return environment.StandardOutput
			.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
			.Distinct(StringComparer.Ordinal)
			.ToArray();
	}
}
