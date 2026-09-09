using DevProjex.Application.Services;

namespace DevProjex.Tests.Unit;

public sealed class ProjectCopyExportSourceReadTests
{
	[Fact]
	public async Task PassThroughCopyRejectsSourceChangedAfterTheFirstRead()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var source = workspace.CreateFile("project/source.txt", new string('A', 512 * 1024));
		var output = Path.Combine(workspace.CreateFolder("output"), "copy");
		var originalTimestamp = File.GetLastWriteTimeUtc(source);
		ProjectCopyExportTestHooks.AfterFirstSourceRead = () =>
			File.SetLastWriteTimeUtc(source, originalTimestamp.AddMinutes(1));
		try
		{
			var service = CreateService();
			var request = CreateRequest(project, source, output);
			var exception = await Assert.ThrowsAsync<ProjectCopyExportException>(() =>
				service.ExportAsync(
					request,
					cancellationToken: TestContext.Current.CancellationToken));
			Assert.Equal(ProjectCopyExportError.SourceUnavailable, exception.Error);
			Assert.Equal(ProjectCopyExportService.SourceChangedDuringCopyMessage, exception.Message);
			Assert.False(Directory.Exists(output));
		}
		finally
		{
			ProjectCopyExportTestHooks.AfterFirstSourceRead = null;
		}
	}

	[Fact]
	public async Task UnchangedPassThroughCopyRemainsByteIdentical()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateFolder("project");
		var expected = Enumerable.Range(0, 8192).Select(static value => (byte)(value % 251)).ToArray();
		var source = Path.Combine(project, "source.bin");
		await File.WriteAllBytesAsync(source, expected, TestContext.Current.CancellationToken);
		var output = Path.Combine(workspace.CreateFolder("output"), "copy");

		await CreateService().ExportAsync(
			CreateRequest(project, source, output),
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(
			expected,
			await File.ReadAllBytesAsync(Path.Combine(output, "source.bin"), TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task SourceReaderAllowsAtomicReplacement()
	{
		using var workspace = new TemporaryDirectory();
		var source = workspace.CreateFile("source.txt", "ORIGINAL");
		var replacement = workspace.CreateFile("replacement.txt", "REPLACED");
		await using var reader = ProjectCopyExportService.OpenSourceFile(source);

		File.Replace(replacement, source, destinationBackupFileName: null);

		using var originalReader = new StreamReader(
			reader,
			Encoding.UTF8,
			detectEncodingFromByteOrderMarks: false,
			leaveOpen: true);
		Assert.Equal("ORIGINAL", await originalReader.ReadToEndAsync(
			TestContext.Current.CancellationToken));
		Assert.Equal("REPLACED", await File.ReadAllTextAsync(
			source,
			TestContext.Current.CancellationToken));
	}

	private static ProjectCopyExportService CreateService() =>
		new(new ProjectCopyExportPlanBuilder());

	private static ProjectCopyExportRequest CreateRequest(string project, string source, string output)
	{
		var root = new TreeNodeDescriptor(
			Path.GetFileName(project),
			project,
			true,
			false,
			"folder",
			[new TreeNodeDescriptor(Path.GetFileName(source), source, false, false, "file", [])]);
		return new ProjectCopyExportRequest(
			project,
			Path.GetFileName(project),
			root,
			new HashSet<string>(PathComparer.Default),
			output,
			ProjectCopyExportFormat.Folder,
			ProjectCopyDestinationMode.Exact);
	}
}
