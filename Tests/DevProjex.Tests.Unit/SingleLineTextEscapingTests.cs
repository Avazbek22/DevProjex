namespace DevProjex.Tests.Unit;

public sealed class SingleLineTextEscapingTests
{
	[Fact]
	public void EscapePreservesUnicodeAndEscapesEverySingleLineControl()
	{
		var result = SingleLineTextEscaping.Escape("a\r\n\tb\u001b\u2028😀");

		Assert.Equal("a\\r\\n\\tb\\u001B\\u2028😀", result);
	}

	[Fact]
	public void AppendBoundedStopsBeforePartialEscapeOrUnicodeScalar()
	{
		var escapedControl = new StringBuilder();
		var escapedScalar = new StringBuilder();

		var controlComplete = SingleLineTextEscaping.AppendBounded(
			escapedControl,
			"a\u001bb".AsSpan(),
			maximumAdditionalCharacters: 6);
		var scalarComplete = SingleLineTextEscaping.AppendBounded(
			escapedScalar,
			"a😀b".AsSpan(),
			maximumAdditionalCharacters: 2);

		Assert.False(controlComplete);
		Assert.Equal("a", escapedControl.ToString());
		Assert.False(scalarComplete);
		Assert.Equal("a", escapedScalar.ToString());
	}

	[Fact]
	public void AppendBoundedDoesNotAllocateExpandedSourceText()
	{
		var source = new string('\u001b', 2 * 1024 * 1024);
		var output = new StringBuilder(49_000);
		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

		var complete = SingleLineTextEscaping.AppendBounded(
			output,
			source.AsSpan(),
			maximumAdditionalCharacters: 49_000);
		var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

		Assert.False(complete);
		Assert.InRange(output.Length, 0, 49_000);
		Assert.InRange(allocatedBytes, 0, 256 * 1024);
	}
}
