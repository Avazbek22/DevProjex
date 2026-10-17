using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
var root = Path.GetFullPath(args[0]);
var transport = new StdioClientTransport(new StdioClientTransportOptions
{
	Name = "DevProjex Flatpak",
	Command = "flatpak",
	Arguments = ["run", "io.github.Avazbek22.DevProjex", "mcp", "--root", root],
	WorkingDirectory = root
});
await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
var tree = await client.CallToolAsync("get_tree", cancellationToken: timeout.Token);
var text = string.Join('\n', tree.Content.OfType<TextContentBlock>().Select(static item => item.Text));
if (tree.IsError == true || !text.Contains("Main.kt", StringComparison.Ordinal))
	throw new InvalidOperationException("The installed Flatpak did not return the expected project tree.");
Console.WriteLine("Installed Flatpak MCP client check passed.");
