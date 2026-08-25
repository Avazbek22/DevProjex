using DevProjex.Application.Services;

namespace DevProjex.Tests.Unit;

public sealed class ProjectCopyExportSourceReadTests
{
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
}
