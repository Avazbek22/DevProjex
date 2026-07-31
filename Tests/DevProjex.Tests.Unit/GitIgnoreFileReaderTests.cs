namespace DevProjex.Tests.Unit;

public sealed class GitIgnoreFileReaderTests
{
	public static TheoryData<string, string[]> LineEndingCases => new()
	{
		{ string.Empty, [] },
		{ "alpha\nbeta\n", ["alpha", "beta"] },
		{ "alpha\r\nbeta\r\n", ["alpha", "beta"] },
		{ "alpha\rbeta\r", ["alpha\rbeta\r"] },
		{ "alpha\r\nbeta\rgamma\nlast", ["alpha", "beta\rgamma", "last"] },
		{ "alpha\nbeta", ["alpha", "beta"] },
		{ "\n\n", [string.Empty, string.Empty] }
	};

	[Theory]
	[MemberData(nameof(LineEndingCases))]
	public void SplitLines_UsesOnlyLfAsDelimiterAndRemovesOnlyCrLfCarriageReturn(
		string content,
		string[] expected)
	{
		var actual = GitIgnoreFileReader.SplitLines(content);

		Assert.Equal(expected, actual);
	}

	[Fact]
	public void Read_InvalidUtf8_ThrowsInsteadOfReplacingBytes()
	{
		using var temp = new TemporaryDirectory();
		var path = temp.CreateBinaryFile(".gitignore", [0x66, 0x6F, 0x6F, 0xFF, 0x0A]);

		Assert.Throws<DecoderFallbackException>(() => GitIgnoreFileReader.Read(path));
	}

	[Fact]
	public void Read_Utf8Bom_IsRemovedFromFirstPatternButIncludedInSourceFingerprint()
	{
		using var temp = new TemporaryDirectory();
		var path = temp.CreateBinaryFile(
			".gitignore",
			[0xEF, 0xBB, 0xBF, 0x66, 0x6F, 0x6F, 0x0A]);

		var source = GitIgnoreFileReader.Read(path);

		Assert.Equal("foo\n", source.Content);
		Assert.Equal(7, source.LengthBytes);
		Assert.NotEmpty(source.ContentFingerprint);
	}

	[Fact]
	public void Read_SourceAboveMaximumSize_IsRejectedBeforeContentAllocation()
	{
		using var temp = new TemporaryDirectory();
		var path = Path.Combine(temp.Path, ".gitignore");
		using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			stream.SetLength(GitIgnoreFileReader.MaximumFileSizeBytes + 1);

		Assert.Throws<IOException>(() => GitIgnoreFileReader.Read(path));
	}
}
