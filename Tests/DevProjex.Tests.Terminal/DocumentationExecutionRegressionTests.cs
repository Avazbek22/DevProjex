using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace DevProjex.Tests.Terminal;

public sealed class DocumentationExecutionRegressionTests
{
	[Fact]
	public async Task PublishedDirectCommandExamplesExecuteWithoutMutatingSource()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("source-project");
		workspace.WriteFile("source-project/src/App.cs", "internal sealed class App {}\n");
		workspace.WriteFile("source-project/README.md", "# Sample\n");
		InitializeGitIndex(project);
		var examples = await ExtractPublishedDirectExamplesAsync(
			FindRepositoryRoot(),
			TestContext.Current.CancellationToken);
		Assert.Contains(examples, static example => example.Source == "README.md");
		Assert.Contains(examples, static example => example.Source == "Docs/CommandLine.md");
		Assert.Contains(examples, static example => example.Source == "Docs/CLI-Migration.md");
		Assert.Contains(examples, static example => example.Source.StartsWith(
			"Assets/HelpContent/",
			StringComparison.Ordinal));
		var uniqueCommands = examples
			.Select(static example => example.Command)
			.Distinct(StringComparer.Ordinal)
			.ToArray();
		Assert.True(uniqueCommands.Length >= 12);

		for (var index = 0; index < uniqueCommands.Length; index++)
		{
			var before = ComputeTreeFingerprint(project);
			var arguments = PrepareArguments(
				uniqueCommands[index],
				project,
				workspace.Path,
				index);
			var environment = new TestTerminalEnvironment();
			var exitCode = await new TerminalApplication(
					environment,
					new TerminalServiceFactory(() => workspace.CreateDirectory($"app-data-{index}")))
				.RunAsync(arguments, TestContext.Current.CancellationToken);

			Assert.Equal(
				CommandLineExitCodes.Success,
				exitCode);
			Assert.Empty(environment.StandardError);
			Assert.NotEmpty(environment.StandardOutput);
			Assert.Equal(before, ComputeTreeFingerprint(project));
			AssertObservableResult(
				arguments,
				environment.StandardOutput,
				project);
		}
	}

	private static async Task<IReadOnlyList<DocumentedCommand>> ExtractPublishedDirectExamplesAsync(
		string repositoryRoot,
		CancellationToken cancellationToken)
	{
		var examples = new List<DocumentedCommand>();
		var readme = await File.ReadAllTextAsync(
			Path.Combine(repositoryRoot, "README.md"),
			cancellationToken);
		var sectionStart = readme.IndexOf("## Command Line", StringComparison.Ordinal);
		var sectionEnd = readme.IndexOf("Use it to:", sectionStart, StringComparison.Ordinal);
		Assert.True(sectionStart >= 0 && sectionEnd > sectionStart);
		AddLineExamples(
			examples,
			"README.md",
			readme[sectionStart..sectionEnd]);

		foreach (var fileName in new[] { "CommandLine.md", "CLI-Migration.md" })
		{
			var content = await File.ReadAllTextAsync(
				Path.Combine(repositoryRoot, "Docs", fileName),
				cancellationToken);
			AddLineExamples(examples, $"Docs/{fileName}", content);
		}

		foreach (var helpPath in Directory.EnumerateFiles(
			         Path.Combine(repositoryRoot, "Assets", "HelpContent"),
			         "help.*.txt"))
		{
			var content = await File.ReadAllTextAsync(helpPath, cancellationToken);
			foreach (Match match in Regex.Matches(
				         content,
				         @"`(?<command>devprojex (?:analyze|export context|export project) [^`\r\n]+)`",
				         RegexOptions.CultureInvariant))
			{
				examples.Add(new DocumentedCommand(
					$"Assets/HelpContent/{Path.GetFileName(helpPath)}",
					match.Groups["command"].Value));
			}
		}

		return examples;
	}

	private static void AddLineExamples(
		ICollection<DocumentedCommand> destination,
		string source,
		string content)
	{
		foreach (var command in content
			         .Split('\n')
			         .Select(static line => line.Trim())
			         .Where(IsConcreteDirectCommand))
		{
			destination.Add(new DocumentedCommand(source, command));
		}
	}

	private static bool IsConcreteDirectCommand(string line) =>
		(line.StartsWith("devprojex analyze ", StringComparison.Ordinal) ||
		 line.StartsWith("devprojex export context ", StringComparison.Ordinal) ||
		 line.StartsWith("devprojex export project ", StringComparison.Ordinal)) &&
		!line.Contains('[') &&
		!line.Contains('<');

	private static string[] PrepareArguments(
		string example,
		string project,
		string workspace,
		int index)
	{
		var arguments = example
			.Split(' ', StringSplitOptions.RemoveEmptyEntries)
			.Skip(1)
			.ToArray();
		var projectIndex = arguments[0] == "analyze" ? 1 : 2;
		Assert.True(arguments.Length > projectIndex);
		arguments[projectIndex] = project;
		var outputIndex = FindOutputOption(arguments);
		if (outputIndex >= 0 && arguments[outputIndex + 1] != "-")
		{
			var extension = ResolveOutputExtension(arguments);
			arguments[outputIndex + 1] = Path.Combine(
				workspace,
				$"documented-output-{index}{extension}");
		}

		return [.. arguments, "--language", "en"];
	}

	private static void AssertObservableResult(
		IReadOnlyList<string> arguments,
		string standardOutput,
		string project)
	{
		var outputIndex = FindOutputOption(arguments);
		if (outputIndex < 0 || arguments[outputIndex + 1] == "-")
		{
			if (arguments.Contains("json", StringComparer.Ordinal))
				using (JsonDocument.Parse(standardOutput)) { }
			else if (arguments.Contains("xml", StringComparer.Ordinal))
				_ = System.Xml.Linq.XDocument.Parse(standardOutput);
			return;
		}

		var destination = Path.GetFullPath(arguments[outputIndex + 1]);
		Assert.Equal(destination, standardOutput.Trim());
		if (arguments.Contains("folder", StringComparer.Ordinal))
		{
			Assert.True(Directory.Exists(destination));
			Assert.True(File.Exists(Path.Combine(destination, "src", "App.cs")));
		}
		else if (arguments.Contains("zip", StringComparer.Ordinal))
		{
			Assert.True(File.Exists(destination));
			using var archive = ZipFile.OpenRead(destination);
			Assert.Contains(
				archive.Entries,
				entry => entry.FullName.EndsWith("src/App.cs", StringComparison.Ordinal));
		}
		else
		{
			Assert.True(File.Exists(destination));
			var content = File.ReadAllText(destination);
			Assert.NotEmpty(content);
			if (arguments.Contains("json", StringComparer.Ordinal))
				using (JsonDocument.Parse(content)) { }
			else if (arguments.Contains("xml", StringComparer.Ordinal))
				_ = System.Xml.Linq.XDocument.Parse(content);
		}

		Assert.False(PathUtility.IsPathInside(destination, project));
	}

	private static int FindOutputOption(IReadOnlyList<string> arguments)
	{
		for (var index = 0; index < arguments.Count - 1; index++)
		{
			if (arguments[index] is "-o" or "--output")
				return index;
		}
		return -1;
	}

	private static string ResolveOutputExtension(IReadOnlyCollection<string> arguments)
	{
		if (arguments.Contains("folder", StringComparer.Ordinal))
			return string.Empty;
		if (arguments.Contains("zip", StringComparer.Ordinal))
			return ".zip";
		if (arguments.Contains("json", StringComparer.Ordinal))
			return ".json";
		if (arguments.Contains("xml", StringComparer.Ordinal))
			return ".xml";
		if (arguments.Contains("text", StringComparer.Ordinal))
			return ".txt";
		return ".md";
	}

	private static string ComputeTreeFingerprint(string root)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		foreach (var path in Directory
			         .EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
			         .OrderBy(
				         path => Path.GetRelativePath(root, path),
				         StringComparer.Ordinal))
		{
			var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
			hash.AppendData(
				Directory.Exists(path)
					? [(byte)'D']
					: [(byte)'F']);
			hash.AppendData(Encoding.UTF8.GetBytes(relative));
			if (File.Exists(path))
				hash.AppendData(File.ReadAllBytes(path));
		}

		return Convert.ToHexString(hash.GetHashAndReset());
	}

	private static void InitializeGitIndex(string project)
	{
		RunGit(project, "init", "--quiet");
		RunGit(project, "add", "--all");
	}

	private static void RunGit(string workingDirectory, params string[] arguments)
	{
		var startInfo = new ProcessStartInfo("git")
		{
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);
		using var process = Process.Start(startInfo);
		Assert.NotNull(process);
		process.WaitForExit();
		Assert.True(
			process.ExitCode == 0,
			$"git {string.Join(' ', arguments)} failed: {process.StandardError.ReadToEnd()}");
	}

	private static string FindRepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "DevProjex.sln")))
				return directory.FullName;
			directory = directory.Parent;
		}

		throw new DirectoryNotFoundException("Repository root was not found.");
	}

	private sealed record DocumentedCommand(
		string Source,
		string Command);
}
