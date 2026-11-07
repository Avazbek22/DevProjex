namespace DevProjex.Tests.Terminal;

public sealed class TerminalWorkspaceDiffCommandTests
{
	private static readonly TerminalWorkspaceCommandParseContext Context = new([".cs"]);
	private readonly TerminalWorkspaceCommandParser _parser = new();

	[Theory]
	[InlineData("diff", "changes")]
	[InlineData("diff changes", "changes")]
	[InlineData("diff STAGED", "staged")]
	[InlineData("diff diff:main..feature/review", "diff:main..feature/review")]
	internal void DiffDefaultsToChangesAndNormalizesTheScope(string text, string expected)
	{
		var result = _parser.Parse(text, Context);

		Assert.True(result.IsSuccess, result.Error?.ToString());
		Assert.Equal(TerminalWorkspaceCommandVerb.Diff, result.Command!.Definition.Verb);
		Assert.Equal(expected, result.Command.Text);
	}

	[Theory]
	[InlineData("diff tracked")]
	[InlineData("diff gitignore")]
	[InlineData("diff diff:main...feature")]
	[InlineData("diff diff:-x..HEAD")]
	internal void DiffRefusesAScopeThatIsNotAChangeSet(string text)
	{
		var result = _parser.Parse(text, Context);

		Assert.False(result.IsSuccess);
		Assert.Equal(TerminalWorkspaceCommandErrorCode.UnknownToken, result.Error!.Code);
	}

	[Fact]
	public void DiffRefusesASecondArgument()
	{
		var result = _parser.Parse("diff staged extra", Context);

		Assert.Equal(TerminalWorkspaceCommandErrorCode.UnexpectedArgument, result.Error!.Code);
		Assert.Equal("extra", result.Error.Value);
	}

	[Fact]
	public void DiffCompletesTheThreeScopes()
	{
		var completion = _parser.GetCompletion("diff ", 5, Context);

		Assert.Equal(["changes", "staged", "diff:"], completion.Candidates.Select(static candidate => candidate.Token));
	}

	[Fact]
	public void DiffIsAWorkspaceCommandWithAParsableExample()
	{
		var definition = TerminalWorkspaceCommandCatalog.Get(TerminalWorkspaceCommandVerb.Diff);

		Assert.Equal("diff", definition.Token);
		Assert.Equal(TerminalWorkspaceCommandAvailability.Workspace, definition.Availability);
		Assert.Equal("diff staged", definition.Example);
		Assert.True(_parser.Parse(definition.Example, Context).IsSuccess);
	}
}
