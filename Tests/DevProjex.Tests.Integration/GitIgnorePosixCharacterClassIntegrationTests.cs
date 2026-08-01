namespace DevProjex.Tests.Integration;

public sealed class GitIgnorePosixCharacterClassIntegrationTests
{
	[Fact]
	public void PosixCharacterClassesMatchNativeGitWildmatch()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("repo");
		var patterns = new[]
		{
			"[[:alnum:]].alnum",
			"[[:alpha:]].alpha",
			"[[:blank:]].blank",
			"[[:cntrl:]].cntrl",
			"[[:digit:]].digit",
			"[[:graph:]].graph",
			"[[:lower:]].lower",
			"[[:print:]].print",
			"[[:punct:]].punct",
			"[[:space:]].space",
			"[[:upper:]].upper",
			"[[:xdigit:]].xdigit",
			"[![:digit:]].not-digit",
			"[^[:space:]].not-space",
			"[x[:digit:]_].mixed",
			"[[:DIGIT:]].uppercase-name",
			"[[:word:]].unknown-name",
			"[[:digit].fallback",
			"[[:digit:].unterminated"
		};
		var candidates = new[]
		{
			"A.alnum", "-.alnum",
			"z.alpha", "7.alpha",
			"\t.blank", "x.blank",
			"\u001f.cntrl", " .cntrl",
			"8.digit", "x.digit",
			"!.graph", " .graph",
			"m.lower", "M.lower",
			" .print", "\u007f.print",
			"@.punct", "A.punct",
			"\t.space", "_.space",
			"Q.upper", "q.upper",
			"F.xdigit", "G.xdigit",
			"a.not-digit", "7.not-digit",
			"x.not-space", "\t.not-space",
			"7.mixed", "_.mixed", "q.mixed",
			"1.uppercase-name",
			"a.unknown-name",
			"d.fallback", "1.fallback",
			"1.unterminated"
		};

		RunGit(repositoryRoot, "init", "--quiet");
		RunGit(repositoryRoot, "config", "core.ignoreCase", "false");
		temp.CreateFile("repo/.gitignore", string.Join('\n', patterns) + "\n");
		var matcher = GitIgnoreMatcher.Build(
			repositoryRoot,
			patterns,
			new GitPathComparisonSemantics(IgnoreCase: false, NormalizeUnicode: false));

		AssertMatcherMatchesNative(repositoryRoot, matcher, candidates);
	}

	[Fact]
	public void CharacterClassesAndEscapesMatchNativeGitAsciiCaseFold()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("casefold-repo");
		var patterns = new[]
		{
			"A-literal.case",
			@"\A-escaped.case",
			"[A]-class.case",
			"[Aa]-mixed.case",
			"[A-Z]-range.case",
			"[[:upper:]]-upper-posix.case",
			"[[:lower:]]-lower-posix.case",
			"[[:DIGIT:]]-invalid-posix.case"
		};
		var candidates = new[]
		{
			"A-literal.case", "a-literal.case",
			"A-escaped.case", "a-escaped.case",
			"A-class.case", "a-class.case",
			"A-mixed.case", "a-mixed.case",
			"A-range.case", "a-range.case",
			"A-upper-posix.case", "a-upper-posix.case",
			"A-lower-posix.case", "a-lower-posix.case",
			"1-invalid-posix.case"
		};

		RunGit(repositoryRoot, "init", "--quiet");
		RunGit(repositoryRoot, "config", "core.ignoreCase", "true");
		temp.CreateFile("casefold-repo/.gitignore", string.Join('\n', patterns) + "\n");
		var matcher = GitIgnoreMatcher.Build(
			repositoryRoot,
			patterns,
			new GitPathComparisonSemantics(IgnoreCase: true, NormalizeUnicode: false));

		AssertMatcherMatchesNative(repositoryRoot, matcher, candidates);
	}

	[Fact]
	public void QuestionMarkAndNonAsciiGlobLiteralsMatchNativeGitUtf8Bytes()
	{
		EnsureGitAvailable();
		using var temp = new TemporaryDirectory();
		var repositoryRoot = temp.CreateDirectory("utf8-byte-repo");
		var patterns = new[]
		{
			"one-?.txt",
			"two-??.txt",
			"cyr-one-?.txt",
			"cyr-two-??.txt",
			"emoji-two-??.txt",
			"emoji-four-????.txt",
			"literal-é.txt",
			"glob-é-*.txt"
		};
		var candidates = new[]
		{
			"one-é.txt",
			"two-é.txt",
			"cyr-one-Ж.txt",
			"cyr-two-Ж.txt",
			"emoji-two-😀.txt",
			"emoji-four-😀.txt",
			"literal-é.txt",
			"glob-é-данные.txt"
		};

		RunGit(repositoryRoot, "init", "--quiet");
		RunGit(repositoryRoot, "config", "core.ignoreCase", "false");
		temp.CreateFile("utf8-byte-repo/.gitignore", string.Join('\n', patterns) + "\n");
		var matcher = GitIgnoreMatcher.Build(
			repositoryRoot,
			patterns,
			new GitPathComparisonSemantics(IgnoreCase: false, NormalizeUnicode: false));

		AssertMatcherMatchesNative(repositoryRoot, matcher, candidates);
	}

	private static void AssertMatcherMatchesNative(
		string repositoryRoot,
		GitIgnoreMatcher matcher,
		IReadOnlyList<string> candidates)
	{
		var nativeIgnoredPaths = QueryNativeGitIgnoredPaths(repositoryRoot, candidates);

		Assert.NotEmpty(nativeIgnoredPaths);
		foreach (var candidate in candidates)
		{
			var expected = nativeIgnoredPaths.Contains(candidate);
			var actual = matcher.IsIgnored(
				Path.Combine(repositoryRoot, candidate),
				isDirectory: false,
				candidate);
			Assert.True(
				expected == actual,
				$"Git wildmatch parity failed for '{EscapeForDisplay(candidate)}': native={expected}, matcher={actual}.");
		}
	}

	private static HashSet<string> QueryNativeGitIgnoredPaths(
		string repositoryRoot,
		IReadOnlyList<string> relativePaths)
	{
		var startInfo = CreateGitStartInfo(repositoryRoot);
		startInfo.ArgumentList.Add("-c");
		startInfo.ArgumentList.Add("core.excludesFile=");
		startInfo.ArgumentList.Add("check-ignore");
		startInfo.ArgumentList.Add("--no-index");
		startInfo.ArgumentList.Add("--stdin");
		startInfo.ArgumentList.Add("-z");
		using var process = Process.Start(startInfo) ??
		                    throw new InvalidOperationException("Could not start git check-ignore.");
		foreach (var path in relativePaths)
		{
			process.StandardInput.Write(path);
			process.StandardInput.Write('\0');
		}

		process.StandardInput.Close();
		var output = process.StandardOutput.ReadToEnd();
		var error = process.StandardError.ReadToEnd();
		if (!process.WaitForExit(10_000))
		{
			process.Kill(entireProcessTree: true);
			throw new TimeoutException("git check-ignore did not complete within 10 seconds.");
		}

		Assert.True(process.ExitCode is 0 or 1, $"git check-ignore failed ({process.ExitCode}): {error}");
		return output
			.Split('\0', StringSplitOptions.RemoveEmptyEntries)
			.ToHashSet(StringComparer.Ordinal);
	}

	private static void EnsureGitAvailable()
	{
		try
		{
			var result = RunGitCore(Environment.CurrentDirectory, ["--version"]);
			if (result.ExitCode != 0)
				Assert.Skip("Git is not available in this test environment.");
		}
		catch (System.ComponentModel.Win32Exception)
		{
			Assert.Skip("Git is not available in this test environment.");
		}
	}

	private static void RunGit(string workingDirectory, params string[] arguments)
	{
		var result = RunGitCore(workingDirectory, arguments);
		Assert.True(
			result.ExitCode == 0,
			$"git failed ({result.ExitCode}): {result.Error}{result.Output}");
	}

	private static GitProcessResult RunGitCore(
		string workingDirectory,
		IReadOnlyList<string> arguments)
	{
		var startInfo = CreateGitStartInfo(workingDirectory);
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);

		using var process = Process.Start(startInfo) ??
		                    throw new InvalidOperationException("Could not start git.");
		var output = process.StandardOutput.ReadToEnd();
		var error = process.StandardError.ReadToEnd();
		if (!process.WaitForExit(10_000))
		{
			process.Kill(entireProcessTree: true);
			throw new TimeoutException("git did not complete within 10 seconds.");
		}

		return new GitProcessResult(process.ExitCode, output, error);
	}

	private static ProcessStartInfo CreateGitStartInfo(string workingDirectory) => new("git")
	{
		WorkingDirectory = workingDirectory,
		UseShellExecute = false,
		CreateNoWindow = true,
		RedirectStandardInput = true,
		RedirectStandardOutput = true,
		RedirectStandardError = true,
		StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
		StandardOutputEncoding = Encoding.UTF8,
		StandardErrorEncoding = Encoding.UTF8
	};

	private static string EscapeForDisplay(string value) => value
		.Replace("\t", @"\t", StringComparison.Ordinal)
		.Replace("\r", @"\r", StringComparison.Ordinal)
		.Replace("\n", @"\n", StringComparison.Ordinal);

	private sealed record GitProcessResult(int ExitCode, string Output, string Error);
}
