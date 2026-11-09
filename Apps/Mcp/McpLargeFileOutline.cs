using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DevProjex.Application.Dependencies;

namespace DevProjex.Mcp;

/// <param name="HeadLines">How many lines from the start of the file the read returns.</param>
/// <param name="Map">The structure lines that follow them, one entry per line.</param>
internal sealed record McpLargeFileOutline(int HeadLines, string Map, int Entries);

/// <summary>
/// The start and the structure of a long file, returned instead of its whole text when a read
/// names no range. Structure comes from the protected text being returned: declarations for code,
/// top-level keys for JSON, headings for Markdown. A file without at least two structure entries
/// has no outline and is read whole, as before. A structure of more than 40 entries is listed by its
/// top level only, each entry with the number of its nested entries, and the list stays within
/// 2,500 characters.
/// </summary>
internal static partial class McpLargeFileOutlineBuilder
{
	public const int MinimumLines = 300;
	public const int MinimumCharacters = 20_000;
	private const int MaximumHeadLines = 40;
	private const int MaximumHeadCharacters = 3_000;
	private const int MaximumCodeHeadLines = 80;
	private const int MaximumCodeHeadCharacters = 4_000;
	private const int MaximumEntries = 80;
	private const int MaximumNestingShown = 2;
	private const int MaximumFullEntries = 40;
	private const int MaximumMapCharacters = 2_500;

	public static bool Applies(string content) =>
		content.Length > MinimumCharacters && CountLines(content) > MinimumLines;

	public static McpLargeFileOutline? Build(
		string relativePath,
		string content,
		Func<IReadOnlyList<NavigationDeclaration>> navigation,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(content);
		ArgumentNullException.ThrowIfNull(navigation);
		if (!Applies(content))
			return null;
		var lines = content.Split('\n');
		var extension = Path.GetExtension(relativePath).ToLowerInvariant();
		var isCode = extension is not (".json" or ".md" or ".mdx" or ".markdown");
		var entries = extension switch
		{
			".json" => JsonEntries(lines, cancellationToken),
			".md" or ".mdx" or ".markdown" => MarkdownEntries(lines, cancellationToken),
			_ => DeclarationEntries(navigation(), lines.Length)
		};
		if (entries.Count < 2)
			return null;
		var map = FormatMap(entries.Count > MaximumFullEntries ? TopLevelEntries(entries) : entries, cancellationToken);
		// Code keeps its whole preamble, imports included, when it ends early enough.
		var head = isCode && entries[0].StartLine > 1
			? HeadLineCount(lines, Math.Min(entries[0].StartLine - 1, MaximumCodeHeadLines), MaximumCodeHeadCharacters)
			: HeadLineCount(lines, MaximumHeadLines, MaximumHeadCharacters);
		return new McpLargeFileOutline(head, map, entries.Count);
	}

	/// <param name="Kind">The declaration kind shown after the name; null for JSON keys and headings.</param>
	/// <param name="Members">Nested entries, counted only when a large structure is listed by its top level.</param>
	private readonly record struct OutlineEntry(int StartLine, int EndLine, int Depth, string Name, string? Kind, int Members = 0);

	// Entries of the minimal depth, each counting the deeper entries listed after it.
	private static IReadOnlyList<OutlineEntry> TopLevelEntries(IReadOnlyList<OutlineEntry> entries)
	{
		var topLevel = new List<OutlineEntry>();
		foreach (var entry in entries)
		{
			if (entry.Depth == 0)
				topLevel.Add(entry);
			else if (topLevel.Count > 0)
				topLevel[^1] = topLevel[^1] with { Members = topLevel[^1].Members + 1 };
		}
		return topLevel;
	}

	private static string FormatMap(IReadOnlyList<OutlineEntry> entries, CancellationToken cancellationToken)
	{
		// Room for the closing "(+N more)" line, so the whole list stays within its cap.
		var reserved = $"(+{entries.Count.ToString(CultureInfo.InvariantCulture)} more)\n".Length;
		var map = new StringBuilder();
		var written = 0;
		foreach (var entry in entries.Take(MaximumEntries))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var line = FormatEntry(entry);
			var isLast = written == entries.Count - 1;
			if (map.Length + line.Length > MaximumMapCharacters - (isLast ? 0 : reserved))
				break;
			map.Append(line);
			written++;
		}
		if (written < entries.Count)
			map.Append("(+").Append((entries.Count - written).ToString(CultureInfo.InvariantCulture)).Append(" more)\n");
		return map.ToString();
	}

	private static string FormatEntry(OutlineEntry entry)
	{
		var members = entry.Members.ToString(CultureInfo.InvariantCulture);
		var suffix = (entry.Kind, entry.Members) switch
		{
			(null, 0) => string.Empty,
			(null, _) => $" ({members} members)",
			(_, 0) => $" ({entry.Kind})",
			_ => $" ({entry.Kind}, {members} members)"
		};
		return new string(' ', 2 * Math.Min(entry.Depth, MaximumNestingShown - 1)) +
			   entry.StartLine.ToString(CultureInfo.InvariantCulture) + "-" +
			   entry.EndLine.ToString(CultureInfo.InvariantCulture) + " " +
			   McpTextEscaping.EscapeSingleLine(entry.Name) + suffix + "\n";
	}

	private static int HeadLineCount(IReadOnlyList<string> lines, int maximumLines, int maximumCharacters)
	{
		var characters = 0;
		var count = 0;
		while (count < lines.Count && count < maximumLines)
		{
			characters += lines[count].Length + 1;
			if (characters > maximumCharacters && count > 0)
				break;
			count++;
		}
		return Math.Max(count, 1);
	}

	private static IReadOnlyList<OutlineEntry> DeclarationEntries(IReadOnlyList<NavigationDeclaration> declarations, int lineCount)
	{
		var shown = declarations
			.Where(static declaration => declaration.Kind != NavigationSymbolKind.Module &&
										 declaration.StartLine >= 1 && declaration.EndLine >= declaration.StartLine)
			.OrderBy(static declaration => declaration.StartLine)
			.ThenByDescending(static declaration => declaration.EndLine)
			.ToArray();
		var entries = new List<OutlineEntry>(shown.Length);
		foreach (var declaration in shown)
		{
			var depth = shown.Count(other => !ReferenceEquals(other, declaration) &&
											 other.StartLine <= declaration.StartLine && declaration.EndLine <= other.EndLine &&
											 (other.StartLine != declaration.StartLine || other.EndLine != declaration.EndLine));
			if (depth >= MaximumNestingShown)
				continue;
			entries.Add(new OutlineEntry(
				declaration.StartLine,
				Math.Min(declaration.EndLine, lineCount),
				depth,
				declaration.Name,
				KindLabel(declaration.Kind)));
		}
		return entries;
	}

	private static string KindLabel(NavigationSymbolKind kind) => kind switch
	{
		NavigationSymbolKind.Type => "type",
		NavigationSymbolKind.Method => "method",
		NavigationSymbolKind.Property => "property",
		NavigationSymbolKind.Field => "field",
		NavigationSymbolKind.Function => "function",
		_ => "declaration"
	};

	// Keys of the top-level object, each from its line to the line before the next one.
	private static IReadOnlyList<OutlineEntry> JsonEntries(IReadOnlyList<string> lines, CancellationToken cancellationToken)
	{
		var starts = new List<(int Line, string Key)>();
		var depth = 0;
		var lastContentLine = 0;
		for (var index = 0; index < lines.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var line = lines[index];
			if (depth == 1 && JsonKeyRegex().Match(line) is { Success: true } key)
				starts.Add((index + 1, key.Groups["key"].Value));
			depth += JsonDepthChange(line);
			if (line.Trim().Length > 0)
				lastContentLine = index + 1;
		}
		var entries = new List<OutlineEntry>(starts.Count);
		for (var index = 0; index < starts.Count; index++)
		{
			var end = index + 1 < starts.Count ? starts[index + 1].Line - 1 : Math.Max(starts[index].Line, lastContentLine - 1);
			entries.Add(new OutlineEntry(starts[index].Line, Math.Max(starts[index].Line, end), 0, $"\"{starts[index].Key}\"", null));
		}
		return entries;
	}

	private static int JsonDepthChange(string line)
	{
		var change = 0;
		var inString = false;
		for (var index = 0; index < line.Length; index++)
		{
			var character = line[index];
			if (inString)
			{
				if (character == '\\') index++;
				else if (character == '"') inString = false;
				continue;
			}
			switch (character)
			{
				case '"': inString = true; break;
				case '{' or '[': change++; break;
				case '}' or ']': change--; break;
			}
		}
		return change;
	}

	// Headings outside fenced code, each to the line before the next heading of the same or a higher level.
	private static IReadOnlyList<OutlineEntry> MarkdownEntries(IReadOnlyList<string> lines, CancellationToken cancellationToken)
	{
		var headings = new List<(int Line, int Level, string Title)>();
		var inFence = false;
		for (var index = 0; index < lines.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var line = lines[index].TrimEnd('\r');
			if (line.TrimStart().StartsWith("```", StringComparison.Ordinal) || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal))
			{
				inFence = !inFence;
				continue;
			}
			if (!inFence && MarkdownHeadingRegex().Match(line) is { Success: true } heading)
				headings.Add((index + 1, heading.Groups["level"].Value.Length, heading.Groups["title"].Value.Trim()));
		}
		var minimumLevel = headings.Count == 0 ? 1 : headings.Min(static heading => heading.Level);
		var entries = new List<OutlineEntry>(headings.Count);
		var lineTotal = lines.Count > 0 && lines[^1].Length == 0 ? lines.Count - 1 : lines.Count;
		for (var index = 0; index < headings.Count; index++)
		{
			var end = lineTotal;
			for (var next = index + 1; next < headings.Count; next++)
			{
				if (headings[next].Level <= headings[index].Level)
				{
					end = headings[next].Line - 1;
					break;
				}
			}
			var depth = headings[index].Level - minimumLevel;
			if (depth >= MaximumNestingShown)
				continue;
			entries.Add(new OutlineEntry(headings[index].Line, Math.Max(headings[index].Line, end), depth, headings[index].Title, null));
		}
		return entries;
	}

	private static int CountLines(string content)
	{
		var lines = 1;
		foreach (var character in content)
			if (character == '\n') lines++;
		return content.EndsWith('\n') ? lines - 1 : lines;
	}

	[GeneratedRegex("^\\s*\"(?<key>[^\"\\\\]{1,120})\"\\s*:", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
	private static partial Regex JsonKeyRegex();

	[GeneratedRegex("^(?<level>#{1,6})\\s+(?<title>\\S.{0,160})", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
	private static partial Regex MarkdownHeadingRegex();
}
