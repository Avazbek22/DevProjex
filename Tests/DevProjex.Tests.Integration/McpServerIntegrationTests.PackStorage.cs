using DevProjex.Mcp;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DevProjex.Tests.Integration;

public sealed partial class McpServerIntegrationTests
{
	[Fact]
	public async Task PackRegistryReadsTheFileItCreated()
	{
		using var workspace = new TemporaryDirectory();
		await using var registry = new McpPackRegistry(workspace.Path);
		var document = await registry.StoreAsync("owned-pack-content", TestContext.Current.CancellationToken);

		await using var lease = registry.OpenReadDocument(document);
		using var reader = new StreamReader(lease.Stream);
		Assert.Equal("owned-pack-content", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task PackRegistryNeverReadsAHardLinkReplacement()
	{
		using var workspace = new TemporaryDirectory();
		await using var registry = new McpPackRegistry(workspace.Path);
		var document = await registry.StoreAsync("owned-pack-content", TestContext.Current.CancellationToken);
		var externalPath = Path.Combine(workspace.Path, "external.txt");
		const string externalSentinel = "external-hard-link-content-must-not-be-delivered";
		File.WriteAllText(externalPath, externalSentinel);
		var externalMode = OperatingSystem.IsWindows()
			? UnixFileMode.None
			: File.GetUnixFileMode(externalPath);
		var packPath = registry.Resolve(document);
		File.Delete(packPath);
		CreateHardLinkOrSkip(packPath, externalPath);

		var exception = Assert.Throws<McpToolException>(() => registry.OpenReadDocument(document));
		Assert.Equal(McpErrorCodes.PackExpired, exception.Code);
		Assert.Equal(externalSentinel, File.ReadAllText(externalPath));
		if (!OperatingSystem.IsWindows())
			Assert.Equal(externalMode, File.GetUnixFileMode(externalPath));
	}

	[Fact]
	public async Task ReadPackNeverReturnsContentFromALinkedExternalFile()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		File.WriteAllText(Path.Combine(project, "Source.cs"), "class Source { }\n");
		File.WriteAllText(Path.Combine(project, "Large.txt"), new string('x', 60_000));
		await using var server = await McpTestServer.StartAsync(project, workspace.Path);
		var stored = await server.CallAsync("pack_context");
		Assert.NotEqual(true, stored.IsError);
		var packId = ExtractPackId(AllText(stored));
		var productDirectory = McpPackRegistry.ResolveProductDirectory(
			Path.Combine(workspace.Path, "temp"),
			xdgRuntimeDirectory: null,
			Environment.UserName);
		var packPath = Assert.Single(Directory.EnumerateFiles(
			Path.Combine(productDirectory, "mcp"),
			packId + ".pack",
			SearchOption.AllDirectories));
		var externalPath = Path.Combine(workspace.Path, "external.txt");
		const string externalSentinel = "external-unredacted-content-must-not-be-delivered";
		File.WriteAllText(externalPath, externalSentinel);
		var externalMode = OperatingSystem.IsWindows()
			? UnixFileMode.None
			: File.GetUnixFileMode(externalPath);
		File.Delete(packPath);
		CreateFileAliasOrSkip(packPath, externalPath);

		var page = await server.CallAsync(
			"read_pack",
			new Dictionary<string, object?> { ["pack_id"] = packId });
		var response = AllText(page);
		Assert.Equal(true, page.IsError);
		Assert.Contains(McpErrorCodes.PackExpired, response, StringComparison.Ordinal);
		Assert.DoesNotContain(externalSentinel, response, StringComparison.Ordinal);
		Assert.Equal(externalSentinel, File.ReadAllText(externalPath));
		if (!OperatingSystem.IsWindows())
			Assert.Equal(externalMode, File.GetUnixFileMode(externalPath));
	}

	private static void CreateHardLinkOrSkip(string linkPath, string targetPath)
	{
		if (OperatingSystem.IsWindows())
		{
			if (!CreateHardLinkWindows(linkPath, targetPath, IntPtr.Zero))
				Assert.Skip($"Hard-link creation is unavailable: {Marshal.GetLastWin32Error()}.");
			return;
		}

		using var process = Process.Start(new ProcessStartInfo("ln")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardError = true,
			ArgumentList = { targetPath, linkPath }
		});
		if (process is null || !process.WaitForExit(TimeSpan.FromSeconds(10)) || process.ExitCode != 0)
			Assert.Skip("Hard-link creation is unavailable.");
	}

	[DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool CreateHardLinkWindows(string linkPath, string targetPath, IntPtr securityAttributes);
}
