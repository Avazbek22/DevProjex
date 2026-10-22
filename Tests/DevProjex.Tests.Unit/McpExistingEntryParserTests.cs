using DevProjex.Infrastructure.TerminalCommands;

namespace DevProjex.Tests.Unit;

public sealed class McpExistingEntryParserTests
{
	private const string RootToken = "{root}";
	private const string OtherRootToken = "{other}";
	private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "dpx-existing-entry", "project"));
	private static readonly string OtherRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "dpx-existing-entry", "other"));
	private static readonly IReadOnlyDictionary<string, string> NoEnvironment = new Dictionary<string, string>();

	[Theory]
	// The README and the npm package page.
	[InlineData("npx", "npx", false, "-y", "devprojex", "mcp", "--root", RootToken)]
	[InlineData("npx", "npx", false, "devprojex", "mcp", "--root", RootToken)]
	[InlineData("npx", "npx", true, "--yes", "devprojex@5.2.1", "mcp", "--root", RootToken, "--live")]
	[InlineData("npx", "npx", false, "-y", "devprojex@latest", "--", "mcp", "--root", RootToken)]
	[InlineData("npx", "npx", false, "-y", "--prefer-offline", "-q", "DevProjex", "mcp", "--root", RootToken)]
	[InlineData("C:\\Program Files\\nodejs\\npx.cmd", "npx", false, "-y", "devprojex", "mcp", "--root", RootToken)]
	[InlineData("/usr/local/bin/npx", "npx", true, "-y", "devprojex", "mcp", "--live", "--root", RootToken)]
	// Claude Code on Windows starts npm launchers through cmd.
	[InlineData("cmd", "npx", false, "/c", "npx", "-y", "devprojex", "mcp", "--root", RootToken)]
	[InlineData("CMD.EXE", "npx", false, "/d", "/s", "/C", "npx.cmd", "-y", "devprojex", "mcp", "--root", RootToken)]
	[InlineData("cmd", "dnx", true, "/c", "dnx", "devprojex", "mcp", "--root", RootToken, "--live")]
	// The NuGet package page and MCP Registry clients.
	[InlineData("dnx", "dnx", false, "devprojex", "mcp", "--root", RootToken)]
	[InlineData("dnx", "dnx", true, "devprojex@5.2.1", "--yes", "--", "mcp", "--root", RootToken, "--live")]
	[InlineData("dnx", "dnx", false, "--yes", "devprojex", "mcp", "--root", RootToken)]
	[InlineData("dnx", "dnx", false, "devprojex", "--version", "5.2.1", "--prerelease", "--", "mcp", "--root", RootToken)]
	[InlineData("dotnet", "dnx", false, "dnx", "devprojex", "mcp", "--root", RootToken)]
	// Flathub, registered from outside the sandbox or with explicit run options.
	[InlineData("flatpak", "flatpak", false, "run", "io.github.Avazbek22.DevProjex", "mcp", "--root", RootToken)]
	[InlineData("/usr/bin/flatpak", "flatpak", true, "run", "--command=devprojex", "--branch=stable", "io.github.Avazbek22.DevProjex", "mcp", "--root", RootToken, "--live")]
	public void RecognizesEveryDocumentedLauncher(
		string command,
		string expectedLauncher,
		bool expectedLive,
		params string[] arguments)
	{
		var entry = Parse(command, arguments);

		Assert.Equal(McpExistingEntryKind.Launcher, entry.Kind);
		Assert.True(entry.IsDevProjexLauncher);
		Assert.Equal(expectedLauncher, entry.Launcher);
		Assert.Equal(Root, entry.ProjectRoot);
		Assert.Equal(expectedLive ? McpConnectionMode.Live : McpConnectionMode.Standard, entry.Mode);
		Assert.Empty(entry.UnsupportedArguments);
	}

	[Theory]
	[InlineData("node", "server.js")]
	[InlineData("python", "-m", "devprojex", "mcp", "--root", RootToken)]
	[InlineData("npx", "-y", "some-other-server", "mcp", "--root", RootToken)]
	[InlineData("npx", "-y", "devprojexx", "mcp", "--root", RootToken)]
	[InlineData("npx", "-y", "devprojex@", "mcp", "--root", RootToken)]
	[InlineData("npx", "-y", "@scope/devprojex", "mcp", "--root", RootToken)]
	[InlineData("npx", "-y", "devprojex", "tree", RootToken)]
	[InlineData("npx", "-y", "devprojex")]
	[InlineData("npx")]
	[InlineData("npx", "--package=devprojex", "devprojex", "mcp", "--root", RootToken)]
	[InlineData("dnx", "--version", "5.2.1", "mcp", "--root", RootToken)]
	[InlineData("dotnet", "tool", "run", "devprojex", "mcp", "--root", RootToken)]
	[InlineData("cmd", "/c", "cmd", "/c", "npx", "-y", "devprojex", "mcp", "--root", RootToken)]
	[InlineData("cmd", "/k", "npx", "-y", "devprojex", "mcp", "--root", RootToken)]
	[InlineData("cmd", "npx", "-y", "devprojex", "mcp", "--root", RootToken)]
	[InlineData("flatpak", "run", "com.example.Other", "mcp", "--root", RootToken)]
	[InlineData("flatpak", "run", "io.github.Avazbek22.DevProjex.Devel", "mcp", "--root", RootToken)]
	[InlineData("flatpak", "install", "io.github.Avazbek22.DevProjex", "mcp", "--root", RootToken)]
	public void TreatsAnythingElseAsAForeignEntry(string command, params string[] arguments)
	{
		var entry = Parse(command, arguments);

		Assert.Equal(McpExistingEntryKind.Foreign, entry.Kind);
		Assert.False(entry.IsDevProjexLauncher);
		Assert.Null(entry.Launcher);
		Assert.Null(entry.ProjectRoot);
		Assert.Empty(entry.UnsupportedArguments);
	}

	[Fact]
	public void KeepsEveryServerArgumentThatAReplacementWouldDrop()
	{
		var entry = Parse("npx", ["-y", "devprojex", "mcp", "--hide-private-data", "--root", RootToken,
			"--exclude", "default", "--live", "--root", OtherRootToken, "--live"]);

		Assert.Equal(Root, entry.ProjectRoot);
		Assert.Equal(McpConnectionMode.Live, entry.Mode);
		Assert.Equal(new[] { "--hide-private-data", "--exclude", "default", "--root", OtherRoot, "--live" },
			entry.UnsupportedArguments);
	}

	[Theory]
	[InlineData(".")]
	[InlineData("relative/project")]
	[InlineData("~/project")]
	public void ARelativeRootCannotBeComparedAndIsReported(string root)
	{
		var entry = Parse("npx", ["-y", "devprojex", "mcp", "--root", root]);

		Assert.Equal(McpExistingEntryKind.Launcher, entry.Kind);
		Assert.Null(entry.ProjectRoot);
		Assert.Equal(new[] { "--root", root }, entry.UnsupportedArguments);
	}

	[Fact]
	public void ATrailingRootWithoutAValueIsReported()
	{
		var entry = Parse("npx", ["-y", "devprojex", "mcp", "--root"]);

		Assert.Null(entry.ProjectRoot);
		Assert.Equal(new[] { "--root" }, entry.UnsupportedArguments);
	}

	[Theory]
	[InlineData(McpConnectionMode.Standard, false)]
	[InlineData(McpConnectionMode.Standard, true)]
	[InlineData(McpConnectionMode.Live, true)]
	public void AnIdenticalLauncherIsEquivalent(McpConnectionMode mode, bool sharesData)
	{
		var entry = Parse("npx", mode == McpConnectionMode.Live
			? ["-y", "devprojex", "mcp", "--root", RootToken, "--live"]
			: ["-y", "devprojex", "mcp", "--root", RootToken]);

		Assert.True(McpExistingEntryParser.IsEquivalent(entry, Request(mode, Root), sharesData));
	}

	[Fact]
	public void LiveIsNotEquivalentWhenTheLauncherCannotReadThisApplicationsSelection()
	{
		var entry = Parse("npx", ["-y", "devprojex", "mcp", "--root", RootToken, "--live"]);

		Assert.False(McpExistingEntryParser.IsEquivalent(entry, Request(McpConnectionMode.Live, Root), launcherSharesApplicationData: false));
	}

	[Fact]
	public void AnyDifferenceMakesALauncherNotEquivalent()
	{
		var request = Request(McpConnectionMode.Standard, Root);
		Assert.False(McpExistingEntryParser.IsEquivalent(Parse("npx", ["-y", "devprojex", "mcp", "--root", OtherRootToken]), request, true));
		Assert.False(McpExistingEntryParser.IsEquivalent(Parse("npx", ["-y", "devprojex", "mcp", "--root", RootToken, "--live"]), request, true));
		Assert.False(McpExistingEntryParser.IsEquivalent(Parse("npx", ["-y", "devprojex", "mcp", "--root", RootToken, "--hide-private-data"]), request, true));
		Assert.False(McpExistingEntryParser.IsEquivalent(Parse("npx", ["-y", "devprojex", "mcp", "--root", "."]), request, true));
		Assert.False(McpExistingEntryParser.IsEquivalent(Parse("node", ["server.js", "--root", RootToken]), request, true));
		var withEnvironment = McpExistingEntryParser.Parse("npx", Expand(["-y", "devprojex", "mcp", "--root", RootToken]),
			new Dictionary<string, string> { ["DEVPROJEX_LANG"] = "en" });
		Assert.False(McpExistingEntryParser.IsEquivalent(withEnvironment, request, true));
		Assert.False(McpExistingEntryParser.IsEquivalent(Parse("npx", ["-y", "devprojex", "mcp", "--root", RootToken]),
			Request(McpConnectionMode.Live, Root), true));
	}

	[Fact]
	public void TheFingerprintChangesWithTheEntryButNotWithEnvironmentOrder()
	{
		var first = McpExistingEntryParser.Parse("npx", Expand(["-y", "devprojex", "mcp", "--root", RootToken]),
			new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" });
		var reordered = McpExistingEntryParser.Parse("npx", Expand(["-y", "devprojex", "mcp", "--root", RootToken]),
			new Dictionary<string, string> { ["B"] = "2", ["A"] = "1" });

		Assert.Equal(first.Fingerprint, reordered.Fingerprint);
		Assert.Matches("^[0-9A-F]{64}$", first.Fingerprint);
		Assert.NotEqual(first.Fingerprint, McpExistingEntryParser.Parse("npx",
			Expand(["-y", "devprojex", "mcp", "--root", OtherRootToken]), first.Environment).Fingerprint);
		Assert.NotEqual(first.Fingerprint, McpExistingEntryParser.Parse("npx",
			Expand(["-y", "devprojex", "mcp", "--root", RootToken]), new Dictionary<string, string> { ["A"] = "1", ["B"] = "3" }).Fingerprint);
		Assert.NotEqual(first.Fingerprint, McpExistingEntryParser.Parse("pnpx",
			Expand(["-y", "devprojex", "mcp", "--root", RootToken]), first.Environment).Fingerprint);
		// Argument boundaries are part of the identity.
		Assert.NotEqual(
			McpExistingEntryParser.Parse("node", ["a b"], NoEnvironment).Fingerprint,
			McpExistingEntryParser.Parse("node", ["a", "b"], NoEnvironment).Fingerprint);
	}

	[Fact]
	public void TheDisplayedCommandQuotesArgumentsThatWouldOtherwiseBeAmbiguous()
	{
		var entry = McpExistingEntryParser.Parse(
			"C:\\Program Files\\nodejs\\npx.cmd",
			["-y", "devprojex", "mcp", "--root", "/home/person/My \"Best\" Project", ""],
			NoEnvironment);

		Assert.Equal(
			"\"C:\\Program Files\\nodejs\\npx.cmd\" -y devprojex mcp --root \"/home/person/My \\\"Best\\\" Project\" \"\"",
			entry.DisplayCommand);
	}

	[Fact]
	public void TheRawConfigurationIsKeptForAnExactRestore()
	{
		const string raw = "{\"command\":\"npx\",\"args\":[\"-y\",\"devprojex\"],\"timeout\":5}";
		var entry = McpExistingEntryParser.Parse("npx", ["-y", "devprojex"], NoEnvironment, raw);

		Assert.Equal(raw, entry.RawConfiguration);
	}

	private static McpExistingEntry Parse(string command, string[] arguments) =>
		McpExistingEntryParser.Parse(command, Expand(arguments), NoEnvironment);

	private static string[] Expand(string[] arguments) =>
		arguments.Select(static argument => argument switch
		{
			RootToken => Root,
			OtherRootToken => OtherRoot,
			_ => argument
		}).ToArray();

	private static McpConnectionRequest Request(McpConnectionMode mode, string root) =>
		new(McpConnectionClient.ClaudeCode, mode, Path.GetFullPath(Path.Combine(root, "DevProjex.exe")), root);
}
