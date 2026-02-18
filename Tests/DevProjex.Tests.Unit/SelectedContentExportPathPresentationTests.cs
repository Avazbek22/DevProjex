using DevProjex.Application.Services;
using DevProjex.Tests.Unit.Helpers;

namespace DevProjex.Tests.Unit;

public sealed class SelectedContentExportPathPresentationTests
{
	[Fact]
	public void Build_UsesMappedDisplayPath_WhenMapperProvided()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("note.txt", "hello");
		var service = new SelectedContentExportService(new FileContentAnalyzer());

		var result = service.Build(new[] { file }, _ => "https://github.com/user/repo/note.txt");

		Assert.Contains("https://github.com/user/repo/note.txt:", result, StringComparison.Ordinal);
		Assert.DoesNotContain($"{file}:", result, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_UsesOriginalPath_WhenMapperReturnsEmpty()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("note.txt", "hello");
		var service = new SelectedContentExportService(new FileContentAnalyzer());

		var result = service.Build(new[] { file }, _ => string.Empty);

		Assert.Contains($"{file}:", result, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_UsesOriginalPath_WhenMapperThrows()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("note.txt", "hello");
		var service = new SelectedContentExportService(new FileContentAnalyzer());

		var result = service.Build(new[] { file }, _ => throw new InvalidOperationException("mapper failed"));

		Assert.Contains($"{file}:", result, StringComparison.Ordinal);
	}
}

