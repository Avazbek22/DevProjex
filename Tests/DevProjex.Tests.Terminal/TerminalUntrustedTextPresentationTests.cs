using DevProjex.Application.Workspaces;
using DevProjex.Terminal.Execution;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalUntrustedTextPresentationTests
{
	[Fact]
	public void FileSystemNamesNeverRenderTerminalControlCharacters()
	{
		const string unsafeName = "a\u001B\tb\nc";
		const string escapedName = "a\\u001B\\tb\\nc";
		var tree = new TerminalTreeRow(
			new TreeNodeDescriptor(unsafeName, unsafeName, false, false, "file", []),
			Depth: 0,
			IsExpanded: false,
			TerminalTreeCheckState.Checked);
		var picker = new TerminalPathPickerEntry(unsafeName, unsafeName, false, false);
		var workspace = new RecentWorkspaceDescriptor(
			RecentWorkspaceKind.Folder,
			unsafeName,
			unsafeName,
			unsafeName,
			"folder:test",
			DateTimeOffset.UnixEpoch);
		var recent = new TerminalRecentWorkspaceRow(
			workspace,
			static _ => "folder",
			static _ => "now");
		var repository = new TerminalRecentRepositoryRow(new TerminalRecentRepository(
			"https://example.test/repository.git",
			DateTimeOffset.UnixEpoch,
			new CachedRepository(
				"https://example.test/repository.git",
				unsafeName,
				RepositoryCacheState.Ready)));

		AssertSafe(tree.ToString(), escapedName);
		AssertSafe(picker.ToString(), escapedName);
		AssertSafe(recent.ToString(), escapedName);
		AssertSafe(repository.ToString(), escapedName);
		AssertSafe(TerminalWorkspaceSession.FitPathToWidth(unsafeName, 80), escapedName);
	}

	private static void AssertSafe(string rendered, string expected)
	{
		Assert.Contains(expected, rendered, StringComparison.Ordinal);
		Assert.DoesNotContain('\u001B', rendered);
		Assert.DoesNotContain('\t', rendered);
		Assert.DoesNotContain('\n', rendered);
	}
}
