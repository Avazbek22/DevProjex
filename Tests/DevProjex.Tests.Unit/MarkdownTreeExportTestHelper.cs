namespace DevProjex.Tests.Unit;

internal static class MarkdownTreeExportTestHelper
{
	public static string[] ExtractFilePaths(string markdown)
	{
		var parsed = Parse(markdown);
		return parsed.FilePaths;
	}

	public static string[] ExtractEmptyFolderPaths(string markdown)
	{
		var parsed = Parse(markdown);
		return parsed.EmptyFolderPaths;
	}

	public static void AssertMarkdownTreeContract(string markdown, string expectedRootPath)
	{
		var lines = SplitLines(markdown);
		Assert.True(lines.Length >= 2, "Markdown tree export must contain a root line and a blank separator line.");
		Assert.Equal($"Root: {expectedRootPath.Replace('\\', '/')}", lines[0]);
		Assert.Equal(string.Empty, lines[1]);

		foreach (var line in lines.Skip(2))
		{
			if (line.Length == 0)
				continue;

			Assert.DoesNotContain("\t", line, StringComparison.Ordinal);
			Assert.Equal(line.TrimEnd(), line);

			var indent = CountLeadingSpaces(line);
			Assert.Equal(0, indent % 2);
			Assert.StartsWith("- ", line[indent..], StringComparison.Ordinal);
		}
	}

	public static ParsedMarkdownTree Parse(string markdown)
	{
		var lines = SplitLines(markdown);
		Assert.True(lines.Length >= 2, "Markdown tree export must contain a root line and a blank separator line.");
		Assert.StartsWith("Root: ", lines[0], StringComparison.Ordinal);
		Assert.Equal(string.Empty, lines[1]);

		var filePaths = new List<string>();
		var emptyFolderCandidates = new HashSet<string>(StringComparer.Ordinal);
		var foldersWithChildren = new HashSet<string>(StringComparer.Ordinal);
		var folderStack = new List<string>();

		foreach (var line in lines.Skip(2))
		{
			if (line.Length == 0)
				continue;

			var indent = CountLeadingSpaces(line);
			Assert.Equal(0, indent % 2);
			var level = indent / 2;
			Assert.True(level <= folderStack.Count, "Markdown tree item skipped a nesting level.");
			Assert.StartsWith("- ", line[indent..], StringComparison.Ordinal);

			while (folderStack.Count > level)
				folderStack.RemoveAt(folderStack.Count - 1);

			var text = line[(indent + 2)..];
			var isDirectory = text.EndsWith("/", StringComparison.Ordinal);
			var name = UnescapeListText(isDirectory ? text[..^1] : text);
			var parentPath = level == 0 ? string.Empty : folderStack[level - 1];
			if (!string.IsNullOrEmpty(parentPath))
				foldersWithChildren.Add(parentPath);

			var path = Combine(parentPath, name);
			if (isDirectory)
			{
				emptyFolderCandidates.Add(path);
				folderStack.Add(path);
			}
			else
			{
				filePaths.Add(path);
			}
		}

		foreach (var folderWithChildren in foldersWithChildren)
			emptyFolderCandidates.Remove(folderWithChildren);

		return new ParsedMarkdownTree(filePaths.ToArray(), emptyFolderCandidates.ToArray());
	}

	private static string[] SplitLines(string text)
		=> text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

	private static int CountLeadingSpaces(string line)
	{
		var count = 0;
		while (count < line.Length && line[count] == ' ')
			count++;
		return count;
	}

	private static string UnescapeListText(string text)
	{
		if (text.StartsWith('\\') && text.Length > 1 && text[1] is '-' or '*' or '+' or '[')
			return text[1..];

		return text.Replace("\\t", "\t").Replace("\\n", "\n").Replace("\\r", "\r");
	}

	private static string Combine(string prefix, string name)
		=> string.IsNullOrEmpty(prefix) ? name : $"{prefix}/{name}";

	public sealed record ParsedMarkdownTree(string[] FilePaths, string[] EmptyFolderPaths);
}
