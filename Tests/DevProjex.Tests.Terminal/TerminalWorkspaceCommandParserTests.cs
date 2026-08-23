using Terminal.Gui.Input;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalWorkspaceCommandParserTests
{
	private static readonly TerminalWorkspaceCommandParseContext Context = new(
		[".cs", ".md", ".generated"]);
	private readonly TerminalWorkspaceCommandParser _parser = new();

	[Theory]
	[MemberData(nameof(ValidCommands))]
	internal void Parse_RecognizesEveryGrammarForm(
		string text,
		TerminalWorkspaceCommandVerb expectedVerb)
	{
		var result = _parser.Parse(text, Context);

		Assert.True(result.IsSuccess, result.Error?.ToString());
		Assert.Equal(expectedVerb, result.Command!.Definition.Verb);
	}

	[Fact]
	public void Parse_SetUsesStableCatalogTokensAndNormalizesCase()
	{
		var result = _parser.Parse("set HIDE-SECRETS ON", Context);

		Assert.True(result.IsSuccess);
		Assert.Equal("hide-secrets", result.Command!.Target);
		Assert.True(result.Command.Enabled);
	}

	[Fact]
	public void Parse_TypeRejectsAnExtensionOutsideTheCurrentWorkspace()
	{
		var result = _parser.Parse("type .csharp on", Context);

		Assert.False(result.IsSuccess);
		Assert.Equal(TerminalWorkspaceCommandErrorCode.UnknownToken, result.Error!.Code);
		Assert.Equal(5, result.Error.Position);
		Assert.Equal(".csharp", result.Error.Value);
		Assert.Contains(".cs", result.Error.Candidates);
	}

	[Fact]
	public void Parse_TypeDeduplicatesExtensionsWithProjectTokenSemantics()
	{
		var result = _parser.Parse("type .CS .cs .md off", Context);

		Assert.True(result.IsSuccess);
		Assert.Equal([".cs", ".md"], result.Command!.Values);
		Assert.False(result.Command.Enabled);
	}

	[Theory]
	[InlineData("search", "")]
	[InlineData("search hello world", "hello world")]
	[InlineData("search \"hello world\"", "hello world")]
	[InlineData("filter 'generated files'", "generated files")]
	internal void Parse_SearchAndFilterAcceptQuotedOrUnquotedText(string text, string expected)
	{
		var result = _parser.Parse(text, Context);

		Assert.True(result.IsSuccess);
		Assert.Equal(expected, result.Command!.Text);
	}

	[Fact]
	public void Parse_QuotedDestinationPreservesSpacesAndBackslashes()
	{
		var result = _parser.Parse(
			"export zip \"C:\\exports\\Dev Projex.zip\"",
			Context);

		Assert.True(result.IsSuccess);
		Assert.Equal("C:\\exports\\Dev Projex.zip", result.Command!.Destination);
	}

	[Fact]
	public void Parse_ProfileSavePreservesAQuotedName()
	{
		var result = _parser.Parse("profile save \"My Name\"", Context);

		Assert.True(result.IsSuccess);
		Assert.Equal("save", result.Command!.Target);
		Assert.Equal("My Name", result.Command.Text);
	}

	[Fact]
	public void CompletionCoversCopyArgumentsAndProfileAction()
	{
		var copyView = _parser.GetCompletion("copy tr", 7, Context);
		var copyFormat = _parser.GetCompletion("copy content ma", 15, Context);
		var profile = _parser.GetCompletion("profile ", 8, Context);

		Assert.Contains(copyView.Candidates, candidate => candidate.Token == "tree-content");
		Assert.Contains(copyFormat.Candidates, candidate => candidate.Token == "markdown");
		Assert.Contains(profile.Candidates, candidate => candidate.Token == "save");
	}

	[Theory]
	[MemberData(nameof(InvalidCommands))]
	internal void Parse_ReturnsStructuredErrors(
		string text,
		TerminalWorkspaceCommandErrorCode expectedCode,
		int expectedPosition,
		string? expectedCandidate)
	{
		var result = _parser.Parse(text, Context);

		Assert.False(result.IsSuccess);
		Assert.Equal(expectedCode, result.Error!.Code);
		Assert.Equal(expectedPosition, result.Error.Position);
		if (expectedCandidate is not null)
			Assert.Contains(expectedCandidate, result.Error.Candidates);
	}

	[Fact]
	public void CatalogExamplesRoundTripToTheirOwningDefinitions()
	{
		foreach (var definition in TerminalWorkspaceCommandCatalog.All)
		{
			var result = _parser.Parse(definition.Example, Context);

			Assert.True(result.IsSuccess, $"{definition.Syntax}: {result.Error}");
			Assert.Equal(definition.Id, result.Command!.Definition.Id);
		}
	}

	[Fact]
	public void CompletionOffersVerbsTokensValuesAndExtensionsWithoutExecutingPrefixes()
	{
		var verb = _parser.GetCompletion("se", 2, Context);
		var option = _parser.GetCompletion("set hide-p", 10, Context);
		var value = _parser.GetCompletion("set hide-private-data o", 23, Context);
		var extension = _parser.GetCompletion("type .g", 7, Context);

		Assert.Equal("t", verb.GhostSuffix);
		Assert.Contains(verb.Candidates, candidate => candidate.Token == "set");
		Assert.Equal("rivate-data", option.GhostSuffix);
		Assert.Contains(option.Candidates, candidate => candidate.Token == "hide-private-data");
		Assert.Contains(value.Candidates, candidate => candidate.Token == "on");
		Assert.Contains(value.Candidates, candidate => candidate.Token == "off");
		Assert.Contains(extension.Candidates, candidate => candidate.Token == ".generated");
	}

	[Fact]
	public void CompletionReturnsTheLocalizedSchemaHookAfterAnExactVerb()
	{
		var completion = _parser.GetCompletion("set ", 4, Context);

		Assert.Equal("Terminal.Tui.Command.Set.Schema", completion.SchemaKey);
		Assert.NotEmpty(completion.Candidates);
	}

	[Fact]
	public void CompletionCandidatesReplaceOnlyTheCurrentToken()
	{
		var completion = _parser.GetCompletion("view tr", 7, Context);
		var treeContent = Assert.Single(
			completion.Candidates,
			candidate => candidate.Token == "tree-content");

		Assert.Equal("view tree-content", treeContent.CompletedText);
		Assert.Equal(treeContent.CompletedText.Length, treeContent.CursorPosition);
	}

	[Theory]
	[InlineData(':', true)]
	[InlineData(';', false)]
	[InlineData('a', false)]
	public void ActivationKeyRecognizesOnlyTheCommandPrefix(char character, bool expected)
	{
		Assert.Equal(expected, TerminalWorkspaceCommandKey.IsActivation(new Key(character)));
	}

	public static IEnumerable<object[]> ValidCommands =>
	[
		["set hide-secrets on", TerminalWorkspaceCommandVerb.Set],
		["set smart-ignore off", TerminalWorkspaceCommandVerb.Set],
		["set gitignore on", TerminalWorkspaceCommandVerb.Set],
		["all types off", TerminalWorkspaceCommandVerb.All],
		["all exclusions on", TerminalWorkspaceCommandVerb.All],
		["all content on", TerminalWorkspaceCommandVerb.All],
		["type .cs on", TerminalWorkspaceCommandVerb.Type],
		["type .cs .md off", TerminalWorkspaceCommandVerb.Type],
		["view tree-content", TerminalWorkspaceCommandVerb.View],
		["format markdown", TerminalWorkspaceCommandVerb.Format],
		["search private value", TerminalWorkspaceCommandVerb.Search],
		["search", TerminalWorkspaceCommandVerb.Search],
		["filter generated", TerminalWorkspaceCommandVerb.Filter],
		["filter", TerminalWorkspaceCommandVerb.Filter],
		["export context", TerminalWorkspaceCommandVerb.Export],
		["export context json", TerminalWorkspaceCommandVerb.Export],
		["export context output.md", TerminalWorkspaceCommandVerb.Export],
		["export context markdown output.md", TerminalWorkspaceCommandVerb.Export],
		["export zip output.zip", TerminalWorkspaceCommandVerb.Export],
		["export folder output", TerminalWorkspaceCommandVerb.Export],
		["copy", TerminalWorkspaceCommandVerb.Copy],
		["copy tree-content json", TerminalWorkspaceCommandVerb.Copy],
		["analyze", TerminalWorkspaceCommandVerb.Analyze],
		["branch", TerminalWorkspaceCommandVerb.Branch],
		["branch feature/review", TerminalWorkspaceCommandVerb.Branch],
		["update", TerminalWorkspaceCommandVerb.Update],
		["recent", TerminalWorkspaceCommandVerb.Recent],
		["profile save", TerminalWorkspaceCommandVerb.Profile],
		["profile save \"My Name\"", TerminalWorkspaceCommandVerb.Profile],
		["refresh", TerminalWorkspaceCommandVerb.Refresh],
		["help", TerminalWorkspaceCommandVerb.Help],
		["help export", TerminalWorkspaceCommandVerb.Help],
		["quit", TerminalWorkspaceCommandVerb.Quit]
	];

	public static IEnumerable<object?[]> InvalidCommands =>
	[
		["", TerminalWorkspaceCommandErrorCode.EmptyInput, 0, "set"],
		["sett hide-secrets on", TerminalWorkspaceCommandErrorCode.UnknownVerb, 0, "set"],
		["set", TerminalWorkspaceCommandErrorCode.MissingArgument, 3, "hide-secrets"],
		["set hide-secret on", TerminalWorkspaceCommandErrorCode.UnknownToken, 4, "hide-secrets"],
		["set hide-secrets maybe", TerminalWorkspaceCommandErrorCode.InvalidValue, 17, "on"],
		["set hide-secrets on extra", TerminalWorkspaceCommandErrorCode.UnexpectedArgument, 20, (string?)null],
		["all unknown on", TerminalWorkspaceCommandErrorCode.UnknownToken, 4, "content"],
		["type .cs", TerminalWorkspaceCommandErrorCode.MissingArgument, 8, "on"],
		["view contents", TerminalWorkspaceCommandErrorCode.UnknownToken, 5, "content"],
		["format yaml", TerminalWorkspaceCommandErrorCode.UnknownToken, 7, "xml"],
		["export archive out.zip", TerminalWorkspaceCommandErrorCode.UnknownToken, 7, "zip"],
		["export zip", TerminalWorkspaceCommandErrorCode.MissingArgument, 10, (string?)null],
		["copy contents", TerminalWorkspaceCommandErrorCode.UnknownToken, 5, "content"],
		["copy content yaml", TerminalWorkspaceCommandErrorCode.UnknownToken, 13, "xml"],
		["analyze now", TerminalWorkspaceCommandErrorCode.UnexpectedArgument, 8, (string?)null],
		["branch one two", TerminalWorkspaceCommandErrorCode.UnexpectedArgument, 11, (string?)null],
		["update now", TerminalWorkspaceCommandErrorCode.UnexpectedArgument, 7, (string?)null],
		["recent now", TerminalWorkspaceCommandErrorCode.UnexpectedArgument, 7, (string?)null],
		["profile", TerminalWorkspaceCommandErrorCode.MissingArgument, 7, "save"],
		["profile load", TerminalWorkspaceCommandErrorCode.UnknownToken, 8, "save"],
		["refresh now", TerminalWorkspaceCommandErrorCode.UnexpectedArgument, 8, (string?)null],
		["help unknown", TerminalWorkspaceCommandErrorCode.UnknownToken, 5, "analyze"],
		["quit now", TerminalWorkspaceCommandErrorCode.UnexpectedArgument, 5, (string?)null],
		["search \"unfinished", TerminalWorkspaceCommandErrorCode.UnterminatedQuote, 7, (string?)null]
	];
}
