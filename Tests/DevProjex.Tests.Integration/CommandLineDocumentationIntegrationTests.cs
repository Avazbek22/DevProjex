namespace DevProjex.Tests.Integration;

[Trait("Category", "TerminalCommand")]
public sealed class CommandLineDocumentationIntegrationTests
{
	[Fact]
	public void CommandLineDocumentation_DocumentsPublicTokensAndOutputContract()
	{
		var repositoryRoot = FindRepositoryRoot();
		var docsPath = Path.Combine(repositoryRoot, "Docs", "CommandLine.md");
		var readmePath = Path.Combine(repositoryRoot, "README.md");

		Assert.True(File.Exists(docsPath), "Command-line documentation must exist for the public CLI contract.");

		var docs = File.ReadAllText(docsPath);
		foreach (var token in CommandLineOptionTokens.PublicHelpTokens)
			Assert.Contains(token, docs, StringComparison.Ordinal);

		foreach (var ignoreOptionName in CommandLineOptionTokens.PublicIgnoreOptionNames)
			Assert.Contains(ignoreOptionName, docs, StringComparison.Ordinal);

		foreach (var commandName in CommandLineExecutableAliases.DocumentedCommandNames)
			Assert.Contains(commandName, docs, StringComparison.Ordinal);

		Assert.Contains("--path=<folder>", docs, StringComparison.Ordinal);
		Assert.Contains("--report -", docs, StringComparison.Ordinal);
		Assert.Contains("--strict", docs, StringComparison.Ordinal);
		Assert.Contains("Portable builds ask before changing terminal setup", docs, StringComparison.Ordinal);
		Assert.Contains("Windows Microsoft Store/MSIX", docs, StringComparison.Ordinal);
		Assert.Contains("The alias starts the packaged DevProjex UI executable", docs, StringComparison.Ordinal);
		Assert.Contains("not a separate CLI binary", docs, StringComparison.Ordinal);
		Assert.Contains("Windows portable folder", docs, StringComparison.Ordinal);
		Assert.Contains("%LOCALAPPDATA%\\DevProjex\\bin\\devprojex.cmd", docs, StringComparison.Ordinal);
		Assert.Contains("never edits the machine-wide `PATH`", docs, StringComparison.Ordinal);
		Assert.Contains("Linux installed manually/package", docs, StringComparison.Ordinal);
		Assert.Contains("macOS terminal automation", docs, StringComparison.Ordinal);
		Assert.Contains("Help → Launch from terminal", docs, StringComparison.Ordinal);
		Assert.Contains("~/.local/bin/devprojex", docs, StringComparison.Ordinal);
		Assert.Contains("repairs it silently on startup", docs, StringComparison.Ordinal);
		Assert.Contains("stdout", docs, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("stderr", docs, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("Exit Codes", docs, StringComparison.Ordinal);
		Assert.Contains($"`{CommandLineExitCodes.Success}`", docs, StringComparison.Ordinal);
		Assert.Contains($"`{CommandLineExitCodes.RuntimeError}`", docs, StringComparison.Ordinal);
		Assert.Contains($"`{CommandLineExitCodes.UsageError}`", docs, StringComparison.Ordinal);
		Assert.Contains($"`{CommandLineExitCodes.Canceled}`", docs, StringComparison.Ordinal);
		Assert.Contains("arrays contain files, objects contain subfolders", docs, StringComparison.Ordinal);
		Assert.Contains("`/` contains files in the current folder", docs, StringComparison.Ordinal);
		Assert.DoesNotContain("\"dirs\"", docs, StringComparison.Ordinal);
		Assert.DoesNotContain("\"files\"", docs, StringComparison.Ordinal);

		using var jsonExample = JsonDocument.Parse(ExtractFirstJsonFence(docs));
		JsonTreeExportTestHelper.AssertOnlyRootPathAndTree(jsonExample.RootElement);
		JsonTreeExportTestHelper.AssertNoLegacyTreeContract(jsonExample.RootElement);

		var readme = File.ReadAllText(readmePath);
		Assert.Contains("Docs/CommandLine.md", readme, StringComparison.Ordinal);
	}

	private static string ExtractFirstJsonFence(string markdown)
	{
		const string fenceStart = "```json";
		const string fenceEnd = "```";
		var start = markdown.IndexOf(fenceStart, StringComparison.Ordinal);
		Assert.True(start >= 0, "Expected command-line docs to contain a JSON export example.");
		start += fenceStart.Length;
		var end = markdown.IndexOf(fenceEnd, start, StringComparison.Ordinal);
		Assert.True(end > start, "Expected command-line docs JSON example fence to be closed.");
		return markdown[start..end].Trim();
	}

	private static string FindRepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "README.md")) &&
			    Directory.Exists(Path.Combine(directory.FullName, "Assets")) &&
			    Directory.Exists(Path.Combine(directory.FullName, "Tests")))
			{
				return directory.FullName;
			}

			directory = directory.Parent;
		}

		throw new DirectoryNotFoundException("Could not locate repository root for command-line documentation tests.");
	}
}
