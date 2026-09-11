namespace DevProjex.Tests.Integration;

/// <summary>
/// Search results group their matches under the file path, which stands on its own line, so a
/// line number no longer carries the path as a prefix. An assertion therefore has to bind the
/// numbered line to the block of the file it appears under, which is a stronger statement than
/// the prefix match it replaces: a line number from one file can no longer satisfy an assertion
/// about another.
/// </summary>
internal static class McpSearchOutputAssertions
{
	/// <summary>Asserts that <paramref name="relativePath"/> reported a match on <paramref name="line"/>.</summary>
	internal static void ContainsMatch(string text, string relativePath, int line, string? content = null) =>
		AssertLine(text, relativePath, line, ':', content, expected: true);

	/// <summary>Asserts that <paramref name="relativePath"/> reported <paramref name="line"/> as context.</summary>
	internal static void ContainsContext(string text, string relativePath, int line, string? content = null) =>
		AssertLine(text, relativePath, line, '-', content, expected: true);

	/// <summary>Asserts that <paramref name="relativePath"/> did not report a match on <paramref name="line"/>.</summary>
	internal static void DoesNotContainMatch(string text, string relativePath, int line) =>
		AssertLine(text, relativePath, line, ':', content: null, expected: false);

	/// <summary>Asserts that <paramref name="relativePath"/> did not report <paramref name="line"/> as context.</summary>
	internal static void DoesNotContainContext(string text, string relativePath, int line) =>
		AssertLine(text, relativePath, line, '-', content: null, expected: false);

	/// <summary>Asserts that the file contributed nothing at all to the result.</summary>
	internal static void DoesNotContainFile(string text, string relativePath) =>
		Assert.Null(FindBlock(text, relativePath));

	private static void AssertLine(
		string text,
		string relativePath,
		int line,
		char marker,
		string? content,
		bool expected)
	{
		var block = FindBlock(text, relativePath);
		var needle = FormattableString.Invariant($"{line}{marker}") + content;
		if (!expected)
		{
			if (block is null)
				return;
			Assert.DoesNotContain(block, entry => entry.StartsWith(needle, StringComparison.Ordinal));
			return;
		}

		Assert.NotNull(block);
		Assert.Contains(block!, entry => entry.StartsWith(needle, StringComparison.Ordinal));
	}

	/// <summary>
	/// Returns the lines a file contributed, or null when the file heads no block. A block runs
	/// from its heading to the next heading; group separators and numbered lines belong to it.
	/// </summary>
	private static IReadOnlyList<string>? FindBlock(string text, string relativePath)
	{
		var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
		// A caller may name the file rather than its whole path, the way the prefix assertions
		// this replaces did; a heading matches when it is the path or ends with that segment.
		var start = Array.FindIndex(
			lines,
			line => string.Equals(line, relativePath, StringComparison.Ordinal) ||
			        line.EndsWith("/" + relativePath, StringComparison.Ordinal));
		if (start < 0)
			return null;
		var block = new List<string>();
		for (var index = start + 1; index < lines.Length; index++)
		{
			var line = lines[index];
			if (IsHeading(line))
				break;
			block.Add(line);
		}
		return block;
	}

	/// <summary>A heading is a line that is neither a group separator nor a numbered line.</summary>
	private static bool IsHeading(string line) =>
		line.Length > 0 &&
		!string.Equals(line, "--", StringComparison.Ordinal) &&
		!char.IsAsciiDigit(line[0]);
}
