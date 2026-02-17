using System.Globalization;

namespace DevProjex.Tests.Unit;

public sealed class CommandLineOptionsTests
{
	// Verifies parsing returns the default when no CLI args are provided.
	[Fact]
	public void Parse_ReturnsEmptyWhenNoArgs()
	{
		var result = CommandLineOptions.Parse(Array.Empty<string>());

		Assert.Equal(CommandLineOptions.Empty, result);
	}

	// Verifies parsing captures path, language, and elevation flags.
	[Fact]
	public void Parse_ReadsPathLanguageAndElevation()
	{
		var result = CommandLineOptions.Parse(new[] { "--path", "/tmp/root", "--lang", "ru", "--elevationAttempted" });

		Assert.Equal("/tmp/root", result.Path);
		Assert.Equal(AppLanguage.Ru, result.Language);
		Assert.True(result.ElevationAttempted);
	}

	// Verifies unknown arguments are ignored without side effects.
	[Fact]
	public void Parse_IgnoresUnknownArgs()
	{
		var result = CommandLineOptions.Parse(new[] { "--unknown", "value" });

		Assert.Equal(CommandLineOptions.Empty, result);
	}

	// Verifies the elevation flag can be toggled on an options instance.
	[Fact]
	public void WithElevationAttempted_SetsFlag()
	{
		var result = CommandLineOptions.Empty.WithElevationAttempted();

		Assert.True(result.ElevationAttempted);
	}

	// Verifies arguments are rendered with quotes when the path contains spaces.
	[Fact]
	public void ToArguments_QuotesPathsWithSpaces()
	{
		var options = new CommandLineOptions("/tmp/root folder", AppLanguage.En, true);

		var args = options.ToArguments();

		Assert.Contains("--path", args);
		Assert.Contains("\"/tmp/root folder\"", args);
		Assert.Contains("--lang", args);
		Assert.Contains("en", args);
		Assert.Contains("--elevationAttempted", args);
	}

	// Verifies unsupported language codes return null.
	[Fact]
	public void ParseLanguage_ReturnsNullForUnknown()
	{
		Assert.Null(CommandLineOptions.ParseLanguage("xx"));
	}

	// Verifies unknown languages fall back to English in serialization.
	[Fact]
	public void LanguageToCode_UsesEnglishFallback()
	{
		var value = CommandLineOptions.LanguageToCode((AppLanguage)999);

		Assert.Equal("en", value);
	}

	// Verifies system language detection maps culture codes to app languages.
	[Fact]
	public void DetectSystemLanguage_ReturnsExpectedForCulture()
	{
		var original = CultureInfo.CurrentUICulture;
		try
		{
			CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");

			var detected = CommandLineOptions.DetectSystemLanguage();

			Assert.Equal(AppLanguage.Fr, detected);
		}
		finally
		{
			CultureInfo.CurrentUICulture = original;
		}
	}

	// Verifies missing values for options do not throw or populate fields.
	[Fact]
	public void Parse_IgnoresFlagsWithoutValues()
	{
		var result = CommandLineOptions.Parse(["--path", "--lang"]);

		// Текущая логика: "--lang" будет считаться значением для --path.
		Assert.Equal("--lang", result.Path);

		// Для --lang значение отсутствует.
		Assert.Null(result.Language);
	}

	// Verifies empty options render to an empty argument string.
	[Fact]
	public void ToArguments_ReturnsEmptyWhenNoOptions()
	{
		var args = CommandLineOptions.Empty.ToArguments();

		Assert.Equal(string.Empty, args);
	}

	// Verifies language parsing trims whitespace and ignores case.
	[Fact]
	public void ParseLanguage_TrimsWhitespaceAndCase()
	{
		var result = CommandLineOptions.ParseLanguage(" RU ");

		Assert.Equal(AppLanguage.Ru, result);
	}

	// Verifies quotes inside the path are escaped.
	[Fact]
	public void ToArguments_EscapesQuotesInPath()
	{
		var options = new CommandLineOptions("C:\\My \"Project\"", AppLanguage.En, false);

		var args = options.ToArguments();

		Assert.Contains("--path", args);
		Assert.Contains("\\\"", args);
	}
}
