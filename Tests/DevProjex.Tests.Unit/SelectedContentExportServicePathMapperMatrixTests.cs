namespace DevProjex.Tests.Unit;

public sealed class SelectedContentExportServicePathMapperMatrixTests
{
	[Theory]
	[InlineData("https://github.com/acme/repo/src/main.cs")]
	[InlineData("https://gitlab.com/acme/repo/-/blob/main/src/main.cs")]
	[InlineData("repo://src/main.cs")]
	[InlineData("src/main.cs")]
	[InlineData("SRC/MAIN.CS")]
	[InlineData("mapped-value")]
	[InlineData("mapped value with spaces")]
	[InlineData("mapped-value-123")]
	[InlineData("mapped_value")]
	[InlineData("https://example.local/path?x=1")]
	public void Build_WithPathMapper_UsesMappedHeader(string mappedHeader)
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("main.cs", "class C {}");
		var service = new SelectedContentExportService(new FileContentAnalyzer());

		var output = service.Build([file], _ => mappedHeader);

		Assert.Contains($"{mappedHeader}:", output);
		Assert.DoesNotContain($"{file}:", output);
	}

	[Theory]
	[InlineData(null, false)]
	[InlineData("", false)]
	[InlineData(" ", false)]
	[InlineData("\t", false)]
	[InlineData("\r\n", false)]
	[InlineData(null, true)]
	[InlineData("", true)]
	[InlineData(" ", true)]
	[InlineData("\t", true)]
	[InlineData("\r\n", true)]
	public void Build_WithPathMapperFallback_UsesOriginalHeader(string? mappedHeader, bool throwFromMapper)
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("main.cs", "class C {}");
		var service = new SelectedContentExportService(new FileContentAnalyzer());

		var output = service.Build(
			[file],
			_ =>
			{
				if (throwFromMapper)
					throw new InvalidOperationException("Mapper failure");

				return mappedHeader!;
			});

		Assert.Contains($"{file}:", output);
		Assert.DoesNotContain("Mapper failure", output);
	}

	[Theory]
	[InlineData(true, false, false, 1, true)]
	[InlineData(true, true, false, 1, true)]
	[InlineData(true, false, true, 2, true)]
	[InlineData(false, true, false, 0, false)]
	[InlineData(false, false, true, 1, true)]
	[InlineData(true, true, true, 2, true)]
	public void Build_MixedInput_InvokesMapperOnlyForExportedTextEntries(
		bool includeText,
		bool includeBinary,
		bool includeWhitespace,
		int expectedMapperCalls,
		bool expectNonEmptyOutput)
	{
		using var temp = new TemporaryDirectory();
		var filePaths = new List<string>();

		if (includeText)
			filePaths.Add(temp.CreateFile("main.cs", "class C {}"));
		if (includeBinary)
			filePaths.Add(temp.CreateBinaryFile("logo.bin", [0, 1, 2, 3]));
		if (includeWhitespace)
			filePaths.Add(temp.CreateFile("space.txt", " \t "));

		var mapperCalls = 0;
		var service = new SelectedContentExportService(new FileContentAnalyzer());
		var output = service.Build(
			filePaths,
			path =>
			{
				Interlocked.Increment(ref mapperCalls);
				return $"mapped::{Path.GetFileName(path)}";
			});

		Assert.Equal(expectedMapperCalls, mapperCalls);
		if (expectNonEmptyOutput)
			Assert.False(string.IsNullOrWhiteSpace(output));
		else
			Assert.Equal(string.Empty, output);
	}
}
