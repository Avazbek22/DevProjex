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

	[Fact]
	public async Task NewCommandsAliasesAndShortOptionsParticipateInUnifiedCompletion()
	{
		var roots = await CompleteAsync("devprojex ");
		var exportChildren = await CompleteAsync("devprojex export ");
		var aliasOptions = await CompleteAsync("devprojex export ctx . -");
		var recentOptions = await CompleteAsync("devprojex recent --");
		var recentShortOptions = await CompleteAsync("devprojex recent -");

		Assert.Contains("tree", roots);
		Assert.Contains("recent", roots);
		Assert.Contains("cache", roots);
		Assert.Contains("help", roots);
		Assert.Contains("context", exportChildren);
		Assert.Contains("ctx", exportChildren);
		Assert.Contains("project", exportChildren);
		Assert.Contains("proj", exportChildren);
		Assert.Contains("-f", aliasOptions);
		Assert.Contains("-n", aliasOptions);
		Assert.Contains("--select-from", aliasOptions);
		Assert.Contains("-f", recentShortOptions);
		Assert.Contains("--limit", recentOptions);
	}

	[Fact]
	public async Task HelpCompletionFollowsTheResolvedCommandPath()
	{
		var root = await CompleteAsync("devprojex help ");
		var exportChildren = await CompleteAsync("devprojex help export ");

		Assert.Contains("export", root);
		Assert.Contains("analyze", root);
		Assert.Contains("context", exportChildren);
		Assert.Contains("ctx", exportChildren);
		Assert.Contains("project", exportChildren);
		Assert.Contains("proj", exportChildren);
		Assert.DoesNotContain("analyze", exportChildren);
		Assert.DoesNotContain("cache", exportChildren);
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
		if (shell == "bash")
		{
			Assert.Contains(
				"current_word_argument=(--bash-current-word=\"$2\")",
				environment.StandardOutput,
				StringComparison.Ordinal);
			Assert.Contains(
				"if [[ -n \"${2-}\" ]]",
				environment.StandardOutput,
				StringComparison.Ordinal);
		}
		else
		{
			Assert.DoesNotContain(
				"--bash-current-word",
				environment.StandardOutput,
				StringComparison.Ordinal);
		}
		if (shell is "bash" or "zsh" or "fish")
		{
			var expectedUnit = shell == "bash" ? "utf8-byte" : "unicode-scalar";
			Assert.Contains(
				$"--position-unit {expectedUnit}",
				environment.StandardOutput,
				StringComparison.Ordinal);
		}
		else
		{
			Assert.DoesNotContain(
				"--position-unit",
				environment.StandardOutput,
				StringComparison.Ordinal);
		}
		Assert.DoesNotContain("\u001b", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(environment.StandardError);
	}

	[Theory]
	[InlineData("./Project\\ Space/So", "./Project Space/So")]
	[InlineData("./Literal\\\\Name/So", "./Literal\\Name/So")]
	[InlineData("\"./Project Space/So", "./Project Space/So")]
	public void BashCurrentWordDecoderPreservesShellWordSemantics(
		string rawWord,
		string expected)
	{
		Assert.Equal(expected, BashCompletionWordDecoder.Decode(rawWord));
	}

	[Fact]
	public async Task BashCurrentWordTransportCompletesAnEscapedDirectoryPath()
	{
		using var workspace = new TemporaryDirectory();
		var completionDirectory = workspace.CreateDirectory("completion cwd");
		Directory.CreateDirectory(Path.Combine(completionDirectory, "Project Space", "Source"));
		const string rawCurrentWord = "./Project\\ Space/So";
		const string line = $"devprojex analyze {rawCurrentWord}";
		var encodedWorkingDirectory = Convert.ToBase64String(
			Encoding.UTF8.GetBytes(completionDirectory));
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			[
				"dev",
				"complete",
				"--position",
				line.Length.ToString(CultureInfo.InvariantCulture),
				"--null",
				$"--bash-current-word={rawCurrentWord}",
				"--working-directory-base64",
				encodedWorkingDirectory,
				"--",
				line
			],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardError);
		Assert.Contains(
			"./Project Space/Source/",
			environment.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries));
	}

	[Fact]
	public async Task BashTokenizationIncludesEscapedArgumentsBeforeTheCurrentWord()
	{
		const string line = "devprojex analyze ./Project\\ Space --format j";
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			[
				"dev",
				"complete",
				"--position",
				line.Length.ToString(CultureInfo.InvariantCulture),
				"--bash-current-word=j",
				"--",
				line
			],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardError);
		Assert.Equal(["json"], environment.StandardOutput.Split(
			Environment.NewLine,
			StringSplitOptions.RemoveEmptyEntries));
	}

	[Fact]
	public void LeadingHomeCompletionResolvesAgainstHomeAndPreservesTildePrefix()
	{
		using var workspace = new TemporaryDirectory();
		var homeDirectory = workspace.CreateDirectory("home");
		var baseDirectory = workspace.CreateDirectory("cwd");
		Directory.CreateDirectory(Path.Combine(homeDirectory, "Projects With Space"));

		var candidates = FileSystemCompletionSource.Complete(
			"~/Proj",
			FileSystemCompletionKind.Directories,
			baseDirectory,
			homeDirectory);

		Assert.Equal(["~/Projects With Space/"], candidates);
	}

	[Theory]
	[InlineData(CompletionCursorPositionNormalizer.Utf8ByteUnit)]
	[InlineData(CompletionCursorPositionNormalizer.UnicodeScalarUnit)]
	public void PosixCursorUnitsNormalizeToTheUtf16Cursor(string positionUnit)
	{
		const string line = "devprojex analyze Проект 😀 --format j trailing";
		var expectedPosition = line.IndexOf(" trailing", StringComparison.Ordinal);
		var unicodeScalarPosition = 0;
		foreach (var _ in line.AsSpan(0, expectedPosition).EnumerateRunes())
			unicodeScalarPosition++;
		var sourcePosition = positionUnit == CompletionCursorPositionNormalizer.Utf8ByteUnit
			? Encoding.UTF8.GetByteCount(line.AsSpan(0, expectedPosition))
			: unicodeScalarPosition;

		var normalized = CompletionCursorPositionNormalizer.TryNormalize(
			line,
			sourcePosition,
			positionUnit,
			out var actualPosition);

		Assert.True(normalized);
		Assert.Equal(expectedPosition, actualPosition);
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
			DecodeBase64Candidates(environment.StandardOutput)
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
			DecodeBase64Candidates(environment.StandardOutput));
		Assert.DoesNotContain(
			DecodeBase64Candidates(environment.StandardOutput),
			candidate =>
				candidate.Equals(
					$"Apps{Path.DirectorySeparatorChar}",
					StringComparison.Ordinal) ||
				candidate.Equals(
					$"Application{Path.DirectorySeparatorChar}",
					StringComparison.Ordinal));
	}

	private static string[] DecodeBase64Candidates(string output) =>
		output
			.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
			.Select(static candidate =>
				Encoding.UTF8.GetString(Convert.FromBase64String(candidate)))
			.ToArray();

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

	[Fact]
	public async Task CompletionTransportRejectsConflictingEncodings()
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			["dev", "complete", "--position", "0", "--base64", "--null", "ZA=="],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains(
			"error[DPX-CLI-INVALID-SYNTAX]",
			environment.StandardError,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task NullDelimitedCompletionTransportPreservesUnixControlCharacters()
	{
		if (OperatingSystem.IsWindows())
		{
			Assert.Skip("Windows file names cannot contain the control characters covered by this transport test.");
			return;
		}

		using var workspace = new TemporaryDirectory();
		var completionDirectory = workspace.CreateDirectory("completion cwd");
		const string directoryName = "Project\n\u001b]52;c;ignored\u0007";
		Directory.CreateDirectory(Path.Combine(completionDirectory, directoryName));
		const string line = "devprojex analyze ./Project";
		var encodedWorkingDirectory = Convert.ToBase64String(
			Encoding.UTF8.GetBytes(completionDirectory));
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			[
				"dev",
				"complete",
				"--position",
				line.Length.ToString(CultureInfo.InvariantCulture),
				"--null",
				"--working-directory-base64",
				encodedWorkingDirectory,
				line
			],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardError);
		Assert.Equal(
			[$"./{directoryName}/"],
			environment.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries));
		Assert.EndsWith("\0", environment.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public void FileSystemCompletionRetainsOnlyTheBestCandidatesFromALargeStream()
	{
		const int candidateCount = 100_000;
		const int retainedCount = 200;
		var candidates = Enumerable.Range(0, candidateCount)
			.Reverse()
			.Select(index => new FileSystemCompletionSource.CompletionCandidate(
				$"entry-{index:D6}",
				IsDirectory: index % 2 == 0));

		var retained = FileSystemCompletionSource.SelectBestCandidates(candidates, retainedCount);

		Assert.Equal(retainedCount, retained.Count);
		Assert.All(retained, static candidate => Assert.True(candidate.IsDirectory));
		Assert.Equal("entry-000000", retained[0].Name);
		Assert.Equal("entry-000398", retained[^1].Name);
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
