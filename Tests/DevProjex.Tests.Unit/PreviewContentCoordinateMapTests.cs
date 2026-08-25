using DevProjex.Application.Compression;
using DevProjex.Application.Preview;

namespace DevProjex.Tests.Unit;

public sealed class PreviewContentCoordinateMapTests
{
	[Theory]
	[InlineData("first\nsecond", 1, 6, 12)]
	[InlineData("first\r\nsecond", 1, 6, 13)]
	[InlineData("first\rsecond", 1, 6, 12)]
	[InlineData("first\n", 1, 0, 6)]
	public void IdentityMap_ResolvesColumnsWithoutIncludingLineTerminators(
		string content,
		int line,
		int column,
		int expectedOffset)
	{
		var map = PreviewContentCoordinateMap.Create(content, ContentTransformMap.Identity);

		Assert.True(map.TryToSourceOffset(line, column, out var offset));
		Assert.Equal(expectedOffset, offset);
	}

	[Theory]
	[InlineData("first\nsecond", 0, 6)]
	[InlineData("first\r\nsecond", 0, 6)]
	[InlineData("first\r", 0, 6)]
	public void IdentityMap_RejectsColumnsInsideOrBeyondLineTerminators(
		string content,
		int line,
		int column)
	{
		var map = PreviewContentCoordinateMap.Create(content, ContentTransformMap.Identity);

		Assert.False(map.TryToSourceOffset(line, column, out _));
	}

	[Fact]
	public void CompressedMap_TranslatesAVisibleSelectionBackToCanonicalSourceOffset()
	{
		const string source = "header\nimplementation details\nKEY=secret-value-42\n";
		var removedStart = source.IndexOf("implementation", StringComparison.Ordinal);
		var removedLength = "implementation details\n".Length;
		var plan = CodeCompressionPlan.Create(
			"config.cs",
			"csharp",
			[new CodeCompressionEdit(removedStart, removedLength, "...\n")],
			source.Length,
			"test");
		var compressed = plan.Apply(source);
		var coordinates = PreviewContentCoordinateMap.Create(compressed.Text, compressed.Map);
		var transformedOffset = compressed.Text.IndexOf("secret-value-42", StringComparison.Ordinal);
		var (line, column) = ResolveLineAndColumn(compressed.Text, transformedOffset);

		Assert.True(coordinates.TryToSourceOffset(line, column, out var sourceOffset));
		Assert.Equal(source.IndexOf("secret-value-42", StringComparison.Ordinal), sourceOffset);
	}

	[Fact]
	public void CompressionAndMultilineRedaction_MapLaterSelectionBackToCanonicalSourceOffset()
	{
		const string source = "header\nimplementation details\n-----BEGIN PRIVATE KEY-----\nabc\n-----END PRIVATE KEY-----\nlater=secret-value-42\n";
		var removedStart = source.IndexOf("implementation", StringComparison.Ordinal);
		var compression = CodeCompressionPlan.Create(
			"config.cs",
			"csharp",
			[new CodeCompressionEdit(removedStart, "implementation details\n".Length, "...\n")],
			source.Length,
			"test").Apply(source);
		var pemStart = compression.Text.IndexOf("-----BEGIN PRIVATE KEY-----", StringComparison.Ordinal);
		var pemLength = "-----BEGIN PRIVATE KEY-----\nabc\n-----END PRIVATE KEY-----".Length;
		const string placeholder = "DEVPROJEX_REDACTED[private-key#1]";
		var redactionMap = ContentTransformMap.Create(
			[new ContentTransformRange(pemStart, pemLength, placeholder.Length)],
			compression.Text.Length);
		var finalText = string.Concat(
			compression.Text.AsSpan(0, pemStart),
			placeholder,
			compression.Text.AsSpan(pemStart + pemLength));
		var coordinates = PreviewContentCoordinateMap.Create(
			finalText,
			compression.Map,
			redactionMap);
		var finalOffset = finalText.IndexOf("secret-value-42", StringComparison.Ordinal);
		var (line, column) = ResolveLineAndColumn(finalText, finalOffset);

		Assert.True(coordinates.TryToSourceOffset(line, column, out var sourceOffset));
		Assert.Equal(source.IndexOf("secret-value-42", StringComparison.Ordinal), sourceOffset);
	}

	private static (int Line, int Column) ResolveLineAndColumn(string text, int offset)
	{
		var line = 0;
		var lineStart = 0;
		for (var index = 0; index < offset; index++)
		{
			if (text[index] != '\n')
				continue;
			line++;
			lineStart = index + 1;
		}

		return (line, offset - lineStart);
	}
}
