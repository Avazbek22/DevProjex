namespace DevProjex.Tests.Integration;

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
		Assert.Contains("Portable builds do not edit `PATH` automatically", docs, StringComparison.Ordinal);
		Assert.Contains("Windows Microsoft Store/MSIX", docs, StringComparison.Ordinal);
		Assert.Contains("The alias starts the packaged DevProjex UI executable", docs, StringComparison.Ordinal);
		Assert.Contains("not a separate CLI binary", docs, StringComparison.Ordinal);
		Assert.Contains("Windows portable folder", docs, StringComparison.Ordinal);
		Assert.Contains("Linux installed manually/package", docs, StringComparison.Ordinal);
		Assert.Contains("macOS terminal automation", docs, StringComparison.Ordinal);
		Assert.Contains("Help → Terminal command", docs, StringComparison.Ordinal);
		Assert.Contains("~/.local/bin/devprojex", docs, StringComparison.Ordinal);
		Assert.Contains("detects that state and repairs the wrapper", docs, StringComparison.Ordinal);
		Assert.Contains("stdout", docs, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("stderr", docs, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("Exit Codes", docs, StringComparison.Ordinal);
		Assert.Contains($"`{CommandLineExitCodes.Success}`", docs, StringComparison.Ordinal);
		Assert.Contains($"`{CommandLineExitCodes.RuntimeError}`", docs, StringComparison.Ordinal);
		Assert.Contains($"`{CommandLineExitCodes.UsageError}`", docs, StringComparison.Ordinal);
		Assert.Contains($"`{CommandLineExitCodes.Canceled}`", docs, StringComparison.Ordinal);

		var readme = File.ReadAllText(readmePath);
		Assert.Contains("Docs/CommandLine.md", readme, StringComparison.Ordinal);
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
