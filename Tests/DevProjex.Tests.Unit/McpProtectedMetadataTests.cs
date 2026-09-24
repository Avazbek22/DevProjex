using DevProjex.Mcp;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Unit;

public sealed class McpProtectedMetadataTests
{
	private const string Secret = "ghp_" + "a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL";

	[Fact]
	public void ProjectIndexResolvesBeforeNamesAndPaths()
	{
		using var workspace = new TemporaryDirectory();
		var first = workspace.CreateFolder("first");
		var second = workspace.CreateFolder("second");
		var registry = new McpRootRegistry([first, second]);

		Assert.Equal(registry.Roots[0], registry.ResolveProject("#1"));
		Assert.Equal(registry.Roots[1], registry.ResolveProject("#2"));
	}

	[Theory]
	[InlineData("#0")]
	[InlineData("#3")]
	[InlineData("#999999999999999999999")]
	public void InvalidProjectIndexDoesNotEchoProjectMetadata(string value)
	{
		using var workspace = new TemporaryDirectory();
		var registry = new McpRootRegistry(
			[workspace.CreateFolder(Secret), workspace.CreateFolder("second")]);

		var exception = Assert.Throws<McpToolException>(() => registry.ResolveProject(value));

		Assert.Equal(McpErrorCodes.UnknownProject, exception.Code);
		Assert.DoesNotContain(value, exception.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(Secret, exception.Message, StringComparison.Ordinal);
		Assert.Contains("#1", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void ProtectedJsonMasksStringValuesBeforeSerializingJson()
	{
		var result = McpToolResults.ProtectedJsonSuccess(new
		{
			name = Secret,
			count = 7,
			nested = new[] { "safe", Secret }
		});

		var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
		var openingEnd = text.IndexOf(">\n", StringComparison.Ordinal) + 2;
		var closingStart = text.LastIndexOf("\n</untrusted-data-", StringComparison.Ordinal);
		var json = text[openingEnd..closingStart];
		using var document = JsonDocument.Parse(json);

		Assert.Equal("[redacted]", document.RootElement.GetProperty("name").GetString());
		Assert.Equal(7, document.RootElement.GetProperty("count").GetInt32());
		Assert.Equal("safe", document.RootElement.GetProperty("nested")[0].GetString());
		Assert.Equal("[redacted]", document.RootElement.GetProperty("nested")[1].GetString());
		Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
	}

	[Fact]
	public void ToolErrorMasksDetectedMetadataAndKeepsItsCode()
	{
		var result = McpToolResults.Error(new McpToolException(
			McpErrorCodes.UnknownProject,
			$"{McpErrorCodes.UnknownProject}: project {Secret} is unavailable."));

		var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
		Assert.StartsWith($"{McpErrorCodes.UnknownProject}: request failed.", text, StringComparison.Ordinal);
		Assert.Contains("[redacted]", text, StringComparison.Ordinal);
		Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
	}
}
