using System.Text.RegularExpressions;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task RealProcessReadsEncodedAndNativeRequestArraysIdentically()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/First.cs", "public class First { }\n");
		workspace.WriteFile("project/Second.cs", "public class Second { }\n");
		var requests = new[] { new { path = "First.cs" }, new { path = "Second.cs" } };
		ModelContextProtocol.Protocol.CallToolResult native;
		await using (var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("native-data")))
			native = await CallAsync(server, "get_file", new Dictionary<string, object?> { ["requests"] = requests });
		ModelContextProtocol.Protocol.CallToolResult encoded;
		await using (var server = await ActualMcpProcess.StartAsync(project, workspace.CreateDirectory("encoded-data")))
			encoded = await CallAsync(server, "get_file", new Dictionary<string, object?> { ["requests"] = JsonSerializer.Serialize(requests) });
		Assert.False(native.IsError is true, AllProcessText(native));
		Assert.False(encoded.IsError is true, AllProcessText(encoded));
		var nativeText = Regex.Replace(AllProcessText(native), "untrusted-data-[0-9a-f]+", "untrusted-data-NONCE", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
		var encodedText = Regex.Replace(AllProcessText(encoded), "untrusted-data-[0-9a-f]+", "untrusted-data-NONCE", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
		Assert.Equal(nativeText, encodedText);
		Assert.Contains("public class First", encodedText, StringComparison.Ordinal);
		Assert.Contains("public class Second", encodedText, StringComparison.Ordinal);
	}
}
