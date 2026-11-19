using DevProjex.Infrastructure.ProjectProfiles;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Terminal;

public sealed partial class McpServerProcessTests
{
	[Fact]
	public async Task LiveGetFileOnADirectoryOutsideTheFocusListsItFromTheWindowFiltersNotTheNarrowedFocus()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("live-directory-listing-project");
		workspace.WriteFile("live-directory-listing-project/focus/kept.ts", "export const kept = 1;\n");
		workspace.WriteFile("live-directory-listing-project/outside/a.ts", "export const a = 1;\n");
		workspace.WriteFile("live-directory-listing-project/outside/b.ts", "export const b = 1;\n");
		workspace.WriteFile("live-directory-listing-project/outside/HiddenByCallQz.ts", "export const hidden = 1;\n");
		workspace.WriteFile("live-directory-listing-project/.gitignore", "outside/HiddenByCallQz.ts\n");
		var dataRoot = workspace.CreateDirectory("live-directory-listing-data");
		new ProjectProfileStore(() => dataRoot).SaveProfile(
			project,
			new ProjectSelectionProfile([], [], [], SelectedPaths: ["focus"]));

		await using var server = await ActualMcpProcess.StartAsync(
			project,
			dataRoot,
			["--live"],
			clientInfo: new Implementation { Name = "live-directory-listing-test", Version = "1.0" });

		// "outside" sits outside the saved focus, so a plain file-membership listing (narrowed to
		// the focus) would find nothing there; the directory still exists in the broader window
		// the effective filters allow, and that is what names its entries.
		var directoryRead = await server.Client.CallToolAsync(
			"get_file",
			new Dictionary<string, object?> { ["path"] = "outside" },
			progress: null,
			options: null,
			TestContext.Current.CancellationToken);

		Assert.True(directoryRead.IsError);
		var text = string.Concat(directoryRead.Content
			.OfType<TextContentBlock>()
			.Select(static block => block.Text));
		Assert.Contains("a.ts", text, StringComparison.Ordinal);
		Assert.Contains("b.ts", text, StringComparison.Ordinal);
		// A gitignored file stays hidden by the effective filters even though it physically exists
		// next to the others in the same out-of-focus directory.
		Assert.DoesNotContain("HiddenByCallQz", text, StringComparison.Ordinal);
	}
}
