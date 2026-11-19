using DevProjex.Mcp;

namespace DevProjex.Tests.Terminal;

public sealed class SearchBodyCharactersOffTests
{
	[Theory]
	[InlineData("off")]
	[InlineData("OFF")]
	[InlineData("Off")]
	[InlineData("oFf")]
	public void OffIsAcceptedInAnyCase(string value)
	{
		Assert.Equal(0, McpServerHost.ParseSearchBodyCharacters(value));
	}

	[Theory]
	[InlineData("1", 1)]
	[InlineData("1800", 1800)]
	[InlineData("16000", 16000)]
	public void NumbersInsideTheRangeAreUnchanged(string value, int expected)
	{
		Assert.Equal(expected, McpServerHost.ParseSearchBodyCharacters(value));
	}

	[Theory]
	[InlineData("0")]
	[InlineData("16001")]
	[InlineData("-1")]
	[InlineData(" off")]
	[InlineData("off ")]
	[InlineData("of")]
	[InlineData("offf")]
	[InlineData("none")]
	[InlineData("+5")]
	[InlineData(null)]
	public void ValuesOutsideTheContractAreRejected(string? value)
	{
		Assert.Throws<ArgumentException>(() => McpServerHost.ParseSearchBodyCharacters(value));
	}

	[Theory]
	[InlineData("mcp", "OFF")]
	[InlineData("mcp", "Off")]
	[InlineData("search", "OFF")]
	[InlineData("search", "Off")]
	public void BothCommandsAcceptOffInAnyCase(string command, string value)
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		string[] arguments = command == "mcp"
			? ["mcp", "--search-body-chars", value]
			: ["search", "needle", ".", "--symbols", "--search-body-chars", value];

		var parseResult = root.Parse(arguments);

		Assert.Empty(parseResult.Errors);
	}

	[Theory]
	[InlineData("mcp", "0")]
	[InlineData("mcp", "16001")]
	[InlineData("search", "0")]
	[InlineData("search", "16001")]
	public void BothCommandsKeepTheNumericBounds(string command, string value)
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		string[] arguments = command == "mcp"
			? ["mcp", "--search-body-chars", value]
			: ["search", "needle", ".", "--symbols", "--search-body-chars", value];

		var parseResult = root.Parse(arguments);

		Assert.NotEmpty(parseResult.Errors);
	}
}
