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
	public void PublicHelpTokens_AreUnique()
	{
		Assert.Equal(
			CommandLineOptionTokens.PublicHelpTokens.Count,
			CommandLineOptionTokens.PublicHelpTokens.Distinct(StringComparer.Ordinal).Count());
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
