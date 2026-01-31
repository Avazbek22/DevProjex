using System;
using System.Text;
using DevProjex.Application.Services;
using DevProjex.Tests.Unit.Helpers;
using Xunit;

namespace DevProjex.Tests.Unit;

public sealed class SelectedContentExportServiceMarkerTests
{
	[Fact]
	public void Build_IncludesMarkersAndSeparatorsForMixedFiles()
	{
		using var temp = new TemporaryDirectory();
		var empty = temp.CreateFile("empty.txt", string.Empty);
		var whitespace = temp.CreateFile("space.txt", " \r\n\t");
		var text = temp.CreateFile("text.txt", "Hello");

		var service = new SelectedContentExportService();
		var result = service.Build(new[] { whitespace, text, empty });

		Assert.Contains($"{empty}:", result);
		Assert.Contains("[No Content, 0 bytes]", result);

		var whitespaceBytes = Encoding.UTF8.GetByteCount(" \r\n\t");
		Assert.Contains($"{whitespace}:", result);
		Assert.Contains($"[Whitespace, {whitespaceBytes} bytes]", result);

		Assert.Contains($"{text}:", result);
		Assert.Contains("Hello", result);

		var nl = Environment.NewLine;
		Assert.Contains($"\u00A0{nl}\u00A0{nl}", result);
	}

	[Fact]
	public void Build_SkipsBinaryFilesEvenWhenMixed()
	{
		using var temp = new TemporaryDirectory();
		var binary = temp.CreateBinaryFile("image.bin", new byte[] { 1, 2, 0, 3 });
		var text = temp.CreateFile("note.txt", "text");

		var service = new SelectedContentExportService();
		var result = service.Build(new[] { binary, text });

		Assert.DoesNotContain($"{binary}:", result);
		Assert.Contains($"{text}:", result);
		Assert.Contains("text", result);
	}

	[Fact]
	public void Build_WritesWhitespaceMarkerUsingUtf8ByteCount()
	{
		using var temp = new TemporaryDirectory();
		var content = " \t\r\n";
		var file = temp.CreateFile("whitespace.txt", content);

		var service = new SelectedContentExportService();
		var result = service.Build(new[] { file });

		var bytes = Encoding.UTF8.GetByteCount(content);
		Assert.Contains($"[Whitespace, {bytes} bytes]", result);
	}
}
