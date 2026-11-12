using System.Text;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessLargeStructuresKeepTheirMembersInScalarAndBatchReads()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("members-project");
		var source = new StringBuilder();
		for (var line = 0; line < 70; line++) source.Append("// ").Append('x', 90).Append('\n');
		for (var parent = 0; parent < 3; parent++)
		{
			source.Append($"class Root{parent} {{\n");
			for (var member = 0; member < 16; member++)
			{
				source.Append($"  int M{member}() {{\n");
				source.Append("    // ").Append('x', 160).Append('\n');
				source.Append("    // ").Append('x', 160).Append('\n');
				source.Append($"    return {member};\n  }}\n");
			}
			source.Append("}\n");
		}
		Assert.True(source.Length > 20_000);
		workspace.WriteFile("members-project/Large.cs", source.ToString());
		workspace.WriteFile("members-project/helper.cs", "class Helper {}\n");
		await using var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("data"));
		foreach (var arguments in new Dictionary<string, object?>[]
			{ new() { ["path"] = "Large.cs" }, new() { ["paths"] = new[] { "Large.cs", "helper.cs" } } })
		{
			var result = await CallAsync(server, "get_file", arguments);
			Assert.NotEqual(true, result.IsError);
			var text = BatchData(result);
			Assert.Contains("Structure:\n71-152 Root0 (type, 16 members)\n  72-76 M0 (method)", text, StringComparison.Ordinal);
			Assert.Contains("153-234 Root1 (type, 16 members)", text, StringComparison.Ordinal);
			Assert.Contains("235-316 Root2 (type, 16 members)", text, StringComparison.Ordinal);
			Assert.DoesNotContain("Root0", OutsideSelectionTrustedText(AllProcessText(result)), StringComparison.Ordinal);
		}
	}
}
