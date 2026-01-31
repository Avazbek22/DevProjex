using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DevProjex.Application.Services;
using DevProjex.Tests.Unit.Helpers;
using Xunit;

namespace DevProjex.Tests.Unit;

public sealed class SelectedContentExportServiceAdditionalTests
{
	[Fact]
	// Verifies empty input yields an empty export.
	public void Build_EmptyList_ReturnsEmpty()
	{
		var service = new SelectedContentExportService();

		var output = service.Build(Array.Empty<string>());

		Assert.Equal(string.Empty, output);
	}

	[Fact]
	// Verifies whitespace paths are ignored.
	public void Build_IgnoresWhitespacePaths()
	{
		var service = new SelectedContentExportService();

		var output = service.Build(new[] { " ", "\t", "\n" });

		Assert.Equal(string.Empty, output);
	}

	[Theory]
	// Verifies whitespace-only file contents are exported with a marker.
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("\n")]
	[InlineData("\r\n")]
	[InlineData(" \n ")]
	[InlineData("\t")]
	public void Build_WhitespaceContents_AreMarked(string content)
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("sample.txt", content);
		var service = new SelectedContentExportService();

		var output = service.Build(new[] { file });

		Assert.Contains($"{file}:", output);

		if (content.Length == 0)
		{
			Assert.Contains("[No Content, 0 bytes]", output);
			return;
		}

		var sizeBytes = Encoding.UTF8.GetByteCount(content);
		Assert.Contains($"[Whitespace, {sizeBytes} bytes]", output);
	}

	[Theory]
	// Verifies binary or null-byte contents are skipped.
	[InlineData("\u0000")]
	[InlineData("text\0")]
	[InlineData("\0text")]
	[InlineData("text\0more")]
	public void Build_BinaryContents_AreSkipped(string content)
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("sample.txt", content);
		var service = new SelectedContentExportService();

		var output = service.Build(new[] { file });

		Assert.Equal(string.Empty, output);
	}

	[Theory]
	// Verifies valid file contents are exported with headers.
	[InlineData("Hello")]
	[InlineData("Hello\nWorld")]
	[InlineData("Line1\r\nLine2")]
	[InlineData("Text with spaces  ")]
	[InlineData("  Leading spaces")]
	[InlineData("123")]
	[InlineData("Symbols !@#")]
	[InlineData("Привет")]
	[InlineData("Привет\nмир")]
	[InlineData("A")]
	public void Build_ValidContents_AreIncluded(string content)
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("sample.txt", content);
		var service = new SelectedContentExportService();

		var output = service.Build(new[] { file });

		Assert.Contains($"{file}:", output);
		Assert.Contains(content.TrimEnd('\r', '\n'), output);
	}

	[Theory]
	// Verifies non-existent files are ignored without errors.
	[InlineData("missing.txt")]
	[InlineData("missing/other.txt")]
	[InlineData("nope.bin")]
	[InlineData("file.doesnotexist")]
	[InlineData("empty")]
	public void Build_MissingFiles_AreIgnored(string relativePath)
	{
		using var temp = new TemporaryDirectory();
		var service = new SelectedContentExportService();
		var missing = Path.Combine(temp.Path, relativePath);

		var output = service.Build(new[] { missing });

		Assert.Equal(string.Empty, output);
	}

	[Theory]
	// Verifies files are de-duplicated and ordered case-insensitively.
	[InlineData("b.txt", "a.txt", "a.txt", "b.txt")]
	[InlineData("A.txt", "b.txt", "A.txt", "b.txt")]
	[InlineData("c.txt", "B.txt", "B.txt", "c.txt")]
	[InlineData("d.txt", "C.txt", "C.txt", "d.txt")]
	[InlineData("e.txt", "D.txt", "D.txt", "e.txt")]
	[InlineData("f.txt", "E.txt", "E.txt", "f.txt")]
	[InlineData("g.txt", "F.txt", "F.txt", "g.txt")]
	[InlineData("h.txt", "G.txt", "G.txt", "h.txt")]
	[InlineData("i.txt", "H.txt", "H.txt", "i.txt")]
	[InlineData("j.txt", "I.txt", "I.txt", "j.txt")]
	public void Build_DedupesAndOrders(string first, string second, string expectedFirst, string expectedSecond)
	{
		using var temp = new TemporaryDirectory();
		var fileA = temp.CreateFile(first, "A");
		var fileB = temp.CreateFile(second, "B");
		var service = new SelectedContentExportService();

		var output = service.Build(new[] { fileA, fileB, fileA });

		var comparison = OperatingSystem.IsWindows()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
		var firstIndex = output.IndexOf(Path.Combine(temp.Path, expectedFirst), comparison);
		var secondIndex = output.IndexOf(Path.Combine(temp.Path, expectedSecond), comparison);

		Assert.True(firstIndex >= 0);
		Assert.True(secondIndex > firstIndex);
	}
}
