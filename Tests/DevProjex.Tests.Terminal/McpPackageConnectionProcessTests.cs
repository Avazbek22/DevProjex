using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DevProjex.Tests.Terminal;

public sealed class McpPackageConnectionProcessTests
{
	[Theory]
	[InlineData("npx-cache", "npx")]
	[InlineData("npm-launcher", "npx")]
	[InlineData("nuget-cache", "dnx")]
	[InlineData("nuget-configured-cache", "dnx")]
	[InlineData("nuget-metadata-cache", "dnx")]
	public void RealCliPrintsAndWritesDurableConnectionsFromThePackageLayout(string channel, string command)
	{
		using var workspace = new TemporaryDirectory();
		var source = PublishedApplicationLocator.FindApplicationAssembly(PublishedApplicationHost.Headless);
		var rid = RuntimeInformation.RuntimeIdentifier;
		var windows = OperatingSystem.IsWindows();
		var npmPlatform = (windows ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux") +
			(RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "-arm64" : "-x64");
		var cache = Path.Combine(workspace.Path, "package cache Юникод");
		var relative = command == "npx"
			? (channel == "npx-cache" ? "_npx/hash/" : "global/") + "node_modules/@devprojex/cli-" + npmPlatform + "/bin"
			: (channel == "nuget-cache" ? ".nuget/packages/" : "configured/") + "devprojex." + rid + "/5.2.2/tools/any/" + rid;
		var directory = Path.Combine(cache, relative);
		Directory.CreateDirectory(directory);
		var sourceDirectory = Path.GetDirectoryName(source)!;
		foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
		{
			var destination = Path.Combine(directory, Path.GetRelativePath(sourceDirectory, file));
			Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
			File.Copy(file, destination);
		}
		var host = Path.Combine(directory, Path.GetFileName(source));
		var binary = Path.Combine(directory, windows ? "devprojex.exe" : "devprojex");
		Assert.True(File.Exists(binary), "The headless build must include its apphost.");
		if (channel == "nuget-metadata-cache")
			File.WriteAllText(Path.Combine(cache, "configured/devprojex." + rid + "/5.2.2/.nupkg.metadata"), "{}");
		var project = workspace.CreateDirectory("project with spaces Юникод");
		workspace.WriteFile("project with spaces Юникод/Keep.cs", "class Keep {}\n");
		var data = workspace.CreateDirectory("data");
		string[] prefix = command == "npx" ? ["-y", "devprojex"] : ["devprojex"];
		foreach (var mode in new[] { "standard", "live" })
		{
			var result = Run("json", mode, print: true);
			Assert.Equal(0, result.ExitCode);
			Assert.True(string.IsNullOrWhiteSpace(result.StandardError), result.StandardError);
			using var json = JsonDocument.Parse(result.StandardOutput);
			var entry = json.RootElement.GetProperty("mcpServers").GetProperty("devprojex");
			Assert.Equal(command, entry.GetProperty("command").GetString());
			string[] arguments = [.. prefix, "mcp", "--root", project, .. mode == "live" ? new[] { "--live" } : []];
			Assert.Equal(arguments, entry.GetProperty("args").EnumerateArray().Select(x => x.GetString()));
			Assert.False(Directory.Exists(Path.Combine(project, ".cursor")));
			Assert.False(Directory.Exists(Path.Combine(project, ".vscode")));
		}
		var written = Run("vscode", "live", print: false);
		Assert.Equal(0, written.ExitCode);
		Assert.True(string.IsNullOrWhiteSpace(written.StandardError), written.StandardError);
		using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(project, ".vscode/mcp.json")));
		var server = configuration.RootElement.GetProperty("servers").GetProperty("devprojex");
		Assert.Equal(command, server.GetProperty("command").GetString());
		Assert.Equal([.. prefix, "mcp", "--root", "${workspaceFolder}", "--live"], server.GetProperty("args").EnumerateArray().Select(x => x.GetString()));
		Assert.Equal("class Keep {}\n", File.ReadAllText(Path.Combine(project, "Keep.cs")));

		TerminalTestProcessResult Run(string client, string mode, bool print)
		{
			var start = new ProcessStartInfo("dotnet")
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				WorkingDirectory = project
			};
			foreach (var argument in new[] { host, "mcp", "connect", project, "--client", client, "--mode", mode, "--language", "en" })
				start.ArgumentList.Add(argument);
			if (print)
				start.ArgumentList.Add("--print");
			start.Environment[InvocationEnvironment.InternalDataRootVariable] = data;
			start.Environment["APPIMAGE"] = string.Empty;
			start.Environment["APPIMAGE_EXTRACT_AND_RUN"] = string.Empty;
			start.Environment["FLATPAK_ID"] = string.Empty;
			start.Environment["DEVPROJEX_NPM_EXECUTABLE"] = channel == "npm-launcher" ? binary : string.Empty;
			start.Environment["NUGET_PACKAGES"] = channel == "nuget-configured-cache" ? Path.Combine(cache, "configured") : string.Empty;
			return TerminalTestProcess.Run(start, TimeSpan.FromMinutes(1));
		}
	}
}
