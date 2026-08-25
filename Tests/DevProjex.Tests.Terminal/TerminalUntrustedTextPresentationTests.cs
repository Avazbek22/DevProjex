using DevProjex.Application.Workspaces;
using DevProjex.Terminal.Execution;
using DevProjex.Terminal.Rendering;

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
		AssertSafe(TerminalRecentWorkspacePresentation.DisplayName(workspace), escapedName);
		AssertSafe(TerminalRecentWorkspacePresentation.DisplaySource(workspace), escapedName);
		AssertSafe(TerminalOperationProgressView.SanitizeSource(unsafeName), escapedName);
		AssertSafe(TerminalOperationProgressView.SanitizeLine(unsafeName), escapedName);

		using var data = new TemporaryDirectory();
		var services = new TerminalServiceFactory(() => data.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var exportSummary = new TerminalExportSummary(
			TerminalExportKind.Context,
			ProjectContextView.Tree,
			ProjectContextDocumentFormat.Text,
			unsafeName,
			TerminalExportDestinationState.Ready,
			FileCount: 1,
			FolderCount: 1,
			Bytes: 1,
			Characters: 1,
			EstimatedTokens: 1,
			GitFilteringMode.None,
			Exclusions: [],
			DiagnosticCount: 0);
		var exportText = new TerminalWorkspace(
			services,
			new TestTerminalEnvironment()).BuildExportSummaryText(exportSummary);
		Assert.Contains(escapedName, exportText, StringComparison.Ordinal);
		Assert.DoesNotContain('', exportText);
		Assert.DoesNotContain('	', exportText);
	}

	[Fact]
	public void RecentRepositoryPresentationRemovesCredentials()
	{
		const string secret = "secret-value";
		var repository = new TerminalRecentRepository(
			$"https://user:{secret}@example.test/repository.git",
			DateTimeOffset.UnixEpoch,
			new CachedRepository(
				"https://example.test/repository.git",
				"repository",
				RepositoryCacheState.Ready));

		Assert.Equal("https://example.test/repository.git", repository.SafeDisplayUrl);
		Assert.DoesNotContain(secret, repository.SafeDisplayUrl, StringComparison.Ordinal);
	}

	[Fact]
	public void SingleLineWriterEscapesEveryLineBreakingAndTerminalControlCharacter()
	{
		using var output = new StringWriter();

		TerminalTextEscaping.WriteSingleLine(output, "result\r\n\t\u001B\u2028\u2029");

		Assert.Equal(
			"result\\r\\n\\t\\u001B\\u2028\\u2029" + Environment.NewLine,
			output.ToString());
	}

	private static void AssertSafe(string rendered, string expected)
	{
		Assert.Contains(expected, rendered, StringComparison.Ordinal);
		Assert.DoesNotContain('\u001B', rendered);
		Assert.DoesNotContain('\t', rendered);
		Assert.DoesNotContain('\n', rendered);
	}
}
