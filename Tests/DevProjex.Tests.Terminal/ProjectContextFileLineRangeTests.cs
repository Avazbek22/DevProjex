namespace DevProjex.Tests.Terminal;

public sealed class ProjectContextFileLineRangeTests
{
	[Theory]
	[InlineData(ProjectContextDocumentFormat.Text)]
	[InlineData(ProjectContextDocumentFormat.Markdown)]
	[InlineData(ProjectContextDocumentFormat.Json)]
	[InlineData(ProjectContextDocumentFormat.Xml)]
	public async Task CapturedFileRangesCoverTheirContentWithoutChangingDocumentBytes(
		ProjectContextDocumentFormat format)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var first = workspace.WriteFile("project/src/A.txt", "alpha-unique-content");
		var second = workspace.WriteFile("project/src/B.txt", "beta-unique-content");
		using var services = new TerminalServiceFactory(
				() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var plan = await services.ContextFactory.BuildAsync(
			project,
			new ProjectSelectionSpec(GitMode: GitFilteringMode.None, Exclusions: []),
			cancellationToken: TestContext.Current.CancellationToken);

		await using var baseline = new MemoryStream();
		await services.ContextDocumentService.WriteCompleteWithReportAsync(
			plan,
			ProjectContextView.Content,
			format,
			baseline,
			TestContext.Current.CancellationToken);
		await using var measured = new MemoryStream();
		var result = await services.ContextDocumentService.WriteCompleteWithReportAsync(
			plan,
			ProjectContextView.Content,
			format,
			measured,
			TestContext.Current.CancellationToken,
			captureFileLineRanges: true);

		Assert.Equal(baseline.ToArray(), measured.ToArray());
		var lines = Encoding.UTF8.GetString(measured.ToArray()).Split('\n');
		Assert.Equal(2, result.FileLineRanges.Count);
		AssertCovered(first, "alpha-unique-content", lines, result.FileLineRanges[0]);
		AssertCovered(second, "beta-unique-content", lines, result.FileLineRanges[1]);
	}

	private static void AssertCovered(
		string path,
		string marker,
		IReadOnlyList<string> lines,
		ProjectContextFileLineRange range)
	{
		var line = Array.FindIndex(lines.ToArray(), item => item.Contains(marker, StringComparison.Ordinal)) + 1;
		Assert.True(line > 0);
		Assert.Equal(Path.GetFullPath(path), Path.GetFullPath(range.Path), StringComparer.OrdinalIgnoreCase);
		Assert.InRange(line, range.StartLine, range.EndLine);
	}
}
