using System.Text.RegularExpressions;

namespace DevProjex.Tests.Shared.Git;

/// <summary>
/// A Git started without stdin of its own inherits the stdin of the test process. On Windows it then stops at
/// startup, with one thread in an executive wait and before its trace begins, for as long as anything in the test
/// process waits on a synchronous read of that stdin. A CI job lost every Git for the rest of its run that way.
/// The product already gives Git redirected stdin; this keeps every Git a test starts doing the same.
/// </summary>
public sealed partial class GitTestLaunchContractTests
{
	[Fact]
	public void EveryGitATestStartsHasItsOwnStandardInput()
	{
		var testsRoot = FindTestsRoot();
		var unredirected = Directory
			.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
			.Where(static path => !IsBuildOutput(path))
			.SelectMany(path => FindUnredirectedGitStartInfos(File.ReadAllText(path))
				.Select(line => $"{Path.GetRelativePath(testsRoot, path)}:{line}"))
			.ToArray();

		Assert.Empty(unredirected);
	}

	[Theory]
	[InlineData("var start = new ProcessStartInfo(\"git\") { UseShellExecute = false, RedirectStandardOutput = true };", 1)]
	[InlineData("var start = new ProcessStartInfo { FileName = \"git\", UseShellExecute = false };", 1)]
	[InlineData("var start = new System.Diagnostics.ProcessStartInfo(\"git.exe\")\n{\n\tCreateNoWindow = true\n};", 1)]
	[InlineData("var start = new ProcessStartInfo { FileName = OperatingSystem.IsWindows() ? \"git.exe\" : \"git\" };", 1)]
	[InlineData("var start = new ProcessStartInfo(\"git\") { RedirectStandardInput = true, RedirectStandardOutput = true };", 0)]
	[InlineData("var start = new ProcessStartInfo { FileName = \"git\", RedirectStandardInput = true };", 0)]
	[InlineData("var start = new ProcessStartInfo(\"dotnet\") { UseShellExecute = false };", 0)]
	[InlineData("var start = new ProcessStartInfo(\"git\");", 0)]
	public void TheCheckFindsExactlyTheGitStartInfosWithoutRedirectedInput(string source, int expected) =>
		Assert.Equal(expected, FindUnredirectedGitStartInfos(source).Count());

	internal static IEnumerable<int> FindUnredirectedGitStartInfos(string source)
	{
		foreach (Match match in StartInfoInitializer().Matches(source))
		{
			var body = ReadInitializer(source, match.Index + match.Length - 1);
			var startsGit = match.Groups["git"].Success || FileNameIsGit().IsMatch(body);
			if (startsGit && !body.Contains("RedirectStandardInput = true", StringComparison.Ordinal))
				yield return source.AsSpan(0, match.Index).Count('\n') + 1;
		}
	}

	private static string ReadInitializer(string source, int openingBrace)
	{
		var depth = 0;
		for (var index = openingBrace; index < source.Length; index++)
		{
			if (source[index] == '{')
				depth++;
			else if (source[index] == '}' && --depth == 0)
				return source[openingBrace..(index + 1)];
		}
		return source[openingBrace..];
	}

	private static bool IsBuildOutput(string path)
	{
		var separator = Path.DirectorySeparatorChar;
		return path.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase) ||
			   path.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase);
	}

	private static string FindTestsRoot()
	{
		for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
		{
			var tests = Path.Combine(directory.FullName, "Tests");
			if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) &&
				Directory.Exists(Path.Combine(tests, "Shared", "Git")))
			{
				return tests;
			}
		}
		throw new DirectoryNotFoundException("The repository Tests folder was not found above the test output.");
	}

	[GeneratedRegex(@"new\s+(?:System\.Diagnostics\.)?ProcessStartInfo\s*(?:\(\s*(?:""(?<git>git(?:\.exe)?)""|[^)]*)\s*\))?\s*\{")]
	private static partial Regex StartInfoInitializer();

	[GeneratedRegex(@"FileName\s*=\s*[^,;}]*""git(?:\.exe)?""")]
	private static partial Regex FileNameIsGit();
}
