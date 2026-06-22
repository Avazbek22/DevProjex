namespace DevProjex.Tests.Unit;

public sealed class CommandLineHelpContentProviderTests
{
	[Fact]
	public void GetHelpText_LoadsEmbeddedEnglishHelp()
	{
		var provider = new CommandLineHelpContentProvider();

		var help = provider.GetHelpText();

		Assert.StartsWith("DevProjex", help, StringComparison.Ordinal);
		Assert.Contains("Usage:", help, StringComparison.Ordinal);
		Assert.Contains("Options:", help, StringComparison.Ordinal);
		Assert.Contains("Ignore option names:", help, StringComparison.Ordinal);
	}

	[Fact]
	public void GetHelpText_DocumentsEveryPublicCommandLineToken()
	{
		var help = new CommandLineHelpContentProvider().GetHelpText();

		foreach (var token in CommandLineOptionTokens.PublicHelpTokens)
			Assert.Contains(token, help, StringComparison.Ordinal);
	}

	[Fact]
	public void GetHelpText_DocumentsEveryPublicExecutableAlias()
	{
		var help = new CommandLineHelpContentProvider().GetHelpText();

		foreach (var alias in CommandLineExecutableAliases.DocumentedCommandNames)
			Assert.Contains(alias, help, StringComparison.Ordinal);
	}

	[Fact]
	public void GetHelpText_DocumentsStoreAliasAsUiAppStartupMode()
	{
		var help = new CommandLineHelpContentProvider().GetHelpText();

		Assert.Contains("devprojex.exe starts the packaged DevProjex UI app", help, StringComparison.Ordinal);
		Assert.Contains("--no-ui is a mode of the same app", help, StringComparison.Ordinal);
		Assert.DoesNotContain("DevProjex.Cli", help, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void GetHelpText_DocumentsInlineValueSyntax()
	{
		var help = new CommandLineHelpContentProvider().GetHelpText();

		Assert.Contains("--path=<folder>", help, StringComparison.Ordinal);
	}

	[Fact]
	public void PublicHelpTokens_AreUnique()
	{
		Assert.Equal(
			CommandLineOptionTokens.PublicHelpTokens.Count,
			CommandLineOptionTokens.PublicHelpTokens.Distinct(StringComparer.Ordinal).Count());
	}

	[Fact]
	public void DocumentedCommandNames_AreUniqueStableTerminalNames()
	{
		Assert.Equal(
			CommandLineExecutableAliases.DocumentedCommandNames.Count,
			CommandLineExecutableAliases.DocumentedCommandNames.Distinct(StringComparer.Ordinal).Count());

		foreach (var commandName in CommandLineExecutableAliases.DocumentedCommandNames)
		{
			Assert.Equal(commandName.Trim(), commandName);
			Assert.DoesNotContain(Path.DirectorySeparatorChar, commandName);
			Assert.DoesNotContain(Path.AltDirectorySeparatorChar, commandName);
			Assert.DoesNotContain(' ', commandName);
		}
	}

	[Fact]
	public void PublicAliases_ExposePlatformAutomationNamesOnly()
	{
		Assert.Contains(CommandLineExecutableAliases.DisplayName, CommandLineExecutableAliases.PublicAliases);
		Assert.Contains(CommandLineExecutableAliases.UnixCommand, CommandLineExecutableAliases.PublicAliases);
		Assert.Contains(CommandLineExecutableAliases.WindowsStoreAlias, CommandLineExecutableAliases.PublicAliases);
		Assert.DoesNotContain(CommandLineExecutableAliases.WindowsPortableExecutable, CommandLineExecutableAliases.PublicAliases);
	}

	[Fact]
	public void GetHelpText_DocumentsEveryPublicIgnoreOptionName()
	{
		var help = new CommandLineHelpContentProvider().GetHelpText();

		foreach (var optionName in CommandLineOptionTokens.PublicIgnoreOptionNames)
			Assert.Contains(optionName, help, StringComparison.Ordinal);
	}

	[Fact]
	public void PublicIgnoreOptionNames_AreUnique()
	{
		Assert.Equal(
			CommandLineOptionTokens.PublicIgnoreOptionNames.Count,
			CommandLineOptionTokens.PublicIgnoreOptionNames.Distinct(StringComparer.Ordinal).Count());
	}

	[Fact]
	public void GetHelpText_DoesNotDocumentInternalRelaunchTokens()
	{
		var help = new CommandLineHelpContentProvider().GetHelpText();

		foreach (var token in CommandLineOptionTokens.InternalRelaunchTokens)
			Assert.DoesNotContain(token, help, StringComparison.Ordinal);
	}

	[Fact]
	public void GetHelpText_ReturnsFallbackWhenEmbeddedResourceIsMissing()
	{
		var provider = new CommandLineHelpContentProvider(typeof(CommandLineHelpContentProvider).Assembly);

		var help = provider.GetHelpText();

		Assert.Contains("DevProjex", help, StringComparison.Ordinal);
		Assert.Contains(CommandLineOptionTokens.Help, help, StringComparison.Ordinal);
		Assert.Contains(CommandLineOptionTokens.Path, help, StringComparison.Ordinal);
	}

	[Fact]
	public void GetHelpText_IsStablePlainTextForTerminalOutput()
	{
		var help = new CommandLineHelpContentProvider().GetHelpText();

		Assert.DoesNotContain("\r\n", help, StringComparison.Ordinal);
		Assert.DoesNotContain("\t", help, StringComparison.Ordinal);
		Assert.Equal(help, help.TrimEnd());
	}
}
