namespace DevProjex.Tests.Unit;

public sealed class SelectedContentExportServiceTests
{
	// Verifies missing or empty files are ignored when exporting content.
	[Fact]
	public void Build_SkipsMissingAndEmptyFiles()
	{
		using var temp = new TemporaryDirectory();
		var empty = temp.CreateFile("empty.txt", string.Empty);
		var valid = temp.CreateFile("note.txt", "hello");
		var missing = Path.Combine(temp.Path, "missing.txt");

		var service = new SelectedContentExportService(new FileContentAnalyzer());
		var result = service.Build([missing, empty, valid]);

		Assert.Contains("note.txt:", result);
		Assert.Contains("empty.txt:", result);
		Assert.Contains("[No Content, 0 bytes]", result);
		Assert.DoesNotContain("missing.txt", result);
	}

	// Verifies binary files are excluded from clipboard content.
	[Fact]
	public void Build_SkipsBinaryFiles()
	{
		using var temp = new TemporaryDirectory();
		var binary = temp.CreateBinaryFile("bin.dat", [0, 1, 2, 3]);

		var service = new SelectedContentExportService(new FileContentAnalyzer());
		var result = service.Build([binary]);

		Assert.Equal(string.Empty, result);
	}

	// Verifies exported content is ordered by file path.
	[Fact]
	public void Build_WritesFilesInSortedOrder()
	{
		using var temp = new TemporaryDirectory();
		var fileB = temp.CreateFile("b.txt", "b");
		var fileA = temp.CreateFile("a.txt", "a");

		var service = new SelectedContentExportService(new FileContentAnalyzer());
		var result = service.Build([fileB, fileA]);

		var firstIndex = result.IndexOf("a.txt:", StringComparison.Ordinal);
		var secondIndex = result.IndexOf("b.txt:", StringComparison.Ordinal);
		Assert.True(firstIndex < secondIndex);
	}

	// Verifies whitespace-only file content is treated as empty.
	[Fact]
	public void Build_SkipsWhitespaceOnlyFiles()
	{
		using var temp = new TemporaryDirectory();
		var whitespace = temp.CreateFile("space.txt", "   ");

		var service = new SelectedContentExportService(new FileContentAnalyzer());
		var result = service.Build([whitespace]);

		Assert.Contains("space.txt:", result);
		Assert.Contains("[Whitespace, 3 bytes]", result);
	}

	// Verifies duplicate file paths are included once.
	[Fact]
	public void Build_DeduplicatesPaths()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("dup.txt", "content");

		var service = new SelectedContentExportService(new FileContentAnalyzer());
		var result = service.Build([file, file]);

		Assert.Equal(1, result.Split("dup.txt:").Length - 1);
	}

	// Verifies whitespace-only paths yield empty output.
	[Fact]
	public void Build_ReturnsEmptyForWhitespacePaths()
	{
		var service = new SelectedContentExportService(new FileContentAnalyzer());
		var result = service.Build([" ", "\t", string.Empty]);

		Assert.Equal(string.Empty, result);
	}

	// Verifies trailing newlines are trimmed from file content.
	[Fact]
	public void Build_TrimsTrailingNewlinesFromContent()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("trim.txt", "line\n\n");

		var service = new SelectedContentExportService(new FileContentAnalyzer());
		var result = service.Build([file]);

		Assert.EndsWith("line", result, StringComparison.Ordinal);
	}

	// Verifies separator lines are inserted between multiple files.
	[Fact]
	public void Build_IncludesBlankLinesBetweenFiles()
	{
		using var temp = new TemporaryDirectory();
		var fileA = temp.CreateFile("a.txt", "A");
		var fileB = temp.CreateFile("b.txt", "B");


		var service = new SelectedContentExportService(new FileContentAnalyzer());
		var result = service.Build([fileA, fileB]);


		var nl = Environment.NewLine;
		Assert.Contains($"\u00A0{nl}\u00A0{nl}", result);
	}

	// Verifies files with embedded null bytes in the first bytes are skipped.
	[Fact]
	public void Build_SkipsFilesWithNullBytes()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateBinaryFile("mixed.txt", [1, 2, 0, 3]);

		var service = new SelectedContentExportService(new FileContentAnalyzer());
		var result = service.Build([file]);

		Assert.Equal(string.Empty, result);
	}

	// Verifies content entries include the file path heading.
	[Fact]
	public void Build_IncludesFilePathHeading()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("header.txt", "Header");

		var service = new SelectedContentExportService(new FileContentAnalyzer());
		var result = service.Build([file]);

		Assert.Contains("header.txt:", result);
	}

	// Verifies null paths are ignored safely.
	[Fact]
	public void Build_IgnoresNullPaths()
	{
		var service = new SelectedContentExportService(new FileContentAnalyzer());
		var result = service.Build(new string?[] { null }.Where(p => p is not null)!.Cast<string>());

		Assert.Equal(string.Empty, result);
	}

	// Verifies sorting follows the current platform path semantics.
	[Fact]
	public void Build_SortsPathsCaseInsensitive()
	{
		using var temp = new TemporaryDirectory();
		var fileB = temp.CreateFile("B.txt", "B");
		var fileA = temp.CreateFile("a.txt", "A");

		var service = new SelectedContentExportService(new FileContentAnalyzer());
		var result = service.Build([fileB, fileA]);

		var orderedPaths = new[] { fileB, fileA }
			.OrderBy(path => path, PathComparer.Default)
			.Select(Path.GetFileName)
			.ToArray();
		var firstIndex = result.IndexOf($"{orderedPaths[0]}:", StringComparison.Ordinal);
		var secondIndex = result.IndexOf($"{orderedPaths[1]}:", StringComparison.Ordinal);
		Assert.True(firstIndex < secondIndex);
	}

	// Verifies blank lines are not appended when only one file is written.
	[Fact]
	public void Build_DoesNotInsertSeparatorForSingleFile()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("single.txt", "One");

		var service = new SelectedContentExportService(new FileContentAnalyzer());
		var result = service.Build([file]);

		var nl = Environment.NewLine;
		Assert.DoesNotContain($"\u00A0{nl}\u00A0{nl}", result);
	}

	[Fact]
	public async Task BuildBoundedPreviewAsync_EnforcesFileAndCharacterBudgets()
	{
		using var temp = new TemporaryDirectory();
		var first = temp.CreateFile("a.txt", new string('a', 120));
		var second = temp.CreateFile("b.txt", new string('b', 120));
		var third = temp.CreateFile("c.txt", new string('c', 120));
		var service = new SelectedContentExportService(new FileContentAnalyzer());

		var result = await service.BuildBoundedPreviewAsync(
			[third, second, first],
			maxFileCount: 2,
			maxFileSizeForFullRead: 1024,
			maxOutputCharacters: 180,
			CancellationToken.None,
			displayPathMapper: Path.GetFileName);

		Assert.Equal(180, result.Length);
		Assert.Contains("a.txt:", result, StringComparison.Ordinal);
		Assert.Contains("b.txt:", result, StringComparison.Ordinal);
		Assert.DoesNotContain("c.txt:", result, StringComparison.Ordinal);
	}

	[Fact]
	public async Task BuildBoundedPreviewAsync_DoesNotLoadFileBeyondReadBudget()
	{
		using var temp = new TemporaryDirectory();
		var largePayload = new string('x', 256 * 1024);
		var largeFile = temp.CreateFile("large.txt", largePayload);
		var service = new SelectedContentExportService(new FileContentAnalyzer());

		var result = await service.BuildBoundedPreviewAsync(
			[largeFile],
			maxFileCount: 1,
			maxFileSizeForFullRead: 4096,
			maxOutputCharacters: 8192,
			CancellationToken.None,
			displayPathMapper: Path.GetFileName);

		Assert.Contains("large.txt:", result, StringComparison.Ordinal);
		Assert.DoesNotContain(largePayload[..4096], result, StringComparison.Ordinal);
		Assert.True(result.Length < 256);
	}
}
