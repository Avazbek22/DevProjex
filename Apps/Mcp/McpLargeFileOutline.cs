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
/// top level only, each entry with the number of its nested entries, or, when that level holds one
/// or two entries, by those entries and their direct members; the list stays within 2,500 characters.
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
	private const int MaximumTopLevelExpanded = 2;

	internal static string NormalizeLineEndings(string content) =>
		content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

	public static bool Applies(string content) =>
		content.Length > MinimumCharacters && CountLines(content) > MinimumLines;

	public static McpLargeFileOutline? Build(
		string relativePath,
		string content,
		Func<IReadOnlyList<NavigationDeclaration>> navigation,
		CancellationToken cancellationToken,
		bool requireLargeFile = true)
	{
		ArgumentNullException.ThrowIfNull(content);
		ArgumentNullException.ThrowIfNull(navigation);
		if (requireLargeFile && !Applies(content))
			return null;
		var lines = NormalizeLineEndings(content).Split('\n');
		var extension = Path.GetExtension(relativePath).ToLowerInvariant();
		var isCode = extension is not (".json" or ".md" or ".mdx" or ".markdown");
		// One level deeper than listed, so a member shown in place of its type can count its own members.
		var counted = extension switch
		{
			".json" => JsonEntries(lines, cancellationToken),
			".md" or ".mdx" or ".markdown" => MarkdownEntries(lines, cancellationToken),
			_ => DeclarationEntries(navigation(), lines.Length)
		};
		counted = WithoutParentNames(counted);
		var entries = counted.Where(static entry => entry.Depth < MaximumNestingShown).ToArray();
		if (entries.Length < 2)
			return null;
		var map = FormatMap(entries.Length > MaximumFullEntries ? CompactEntries(counted) : entries, cancellationToken);
		// Code keeps its whole preamble, imports included, when it ends early enough.
		var head = isCode && entries[0].StartLine > 1
			? HeadLineCount(lines, Math.Min(entries[0].StartLine - 1, MaximumCodeHeadLines), MaximumCodeHeadCharacters)
			: HeadLineCount(lines, MaximumHeadLines, MaximumHeadCharacters);
		return new McpLargeFileOutline(head, map, entries.Length);
	}

	/// <param name="Kind">The declaration kind shown after the name; null for JSON keys and headings.</param>
	/// <param name="Members">Nested entries, counted only when a large structure is listed compactly.</param>
	private readonly record struct OutlineEntry(int StartLine, int EndLine, int Depth, string Name, string? Kind, int Members = 0);

	// The top level with member counts; one or two top-level entries are each followed by
	// their direct members, which then carry their own counts.
	private static IReadOnlyList<OutlineEntry> CompactEntries(IReadOnlyList<OutlineEntry> entries)
	{
		var topLevel = WithMemberCounts(entries, 0);
		if (topLevel.Count > MaximumTopLevelExpanded)
			return topLevel.Select(static index => index.Entry).ToArray();
		var members = WithMemberCounts(entries, 1);
		var compact = new List<OutlineEntry>();
		foreach (var parent in topLevel)
		{
			compact.Add(parent.Entry);
			var next = topLevel.Where(other => other.Index > parent.Index).Select(static other => other.Index)
				.DefaultIfEmpty(int.MaxValue).First();
			compact.AddRange(members
				.Where(member => member.Index > parent.Index && member.Index < next)
				.Select(static member => member.Entry));
		}
		return compact;
	}

	private static readonly string[] MemberSeparators = [".", "::", "#"];

	// A nested entry drops the name of the entry it sits in when it repeats it: "Client.send" under "Client" reads "send".
	private static IReadOnlyList<OutlineEntry> WithoutParentNames(IReadOnlyList<OutlineEntry> entries)
	{
		var parents = new List<string>();
		var shortened = new List<OutlineEntry>(entries.Count);
		foreach (var entry in entries)
		{
			if (parents.Count > entry.Depth)
				parents.RemoveRange(entry.Depth, parents.Count - entry.Depth);
			var name = entry.Name;
			if (entry.Depth > 0 && parents.Count == entry.Depth)
				name = WithoutPrefix(name, parents[^1]);
			while (parents.Count < entry.Depth)
				parents.Add(string.Empty);
			parents.Add(entry.Name);
			shortened.Add(entry with { Name = name });
		}
		return shortened;
	}

	private static string WithoutPrefix(string name, string parent)
	{
		if (parent.Length == 0 || !name.StartsWith(parent, StringComparison.Ordinal))
			return name;
		foreach (var separator in MemberSeparators)
		{
			if (string.CompareOrdinal(name, parent.Length, separator, 0, separator.Length) == 0 &&
				name.Length > parent.Length + separator.Length)
				return name[(parent.Length + separator.Length)..];
		}
		return name;
	}

	// Entries of one depth with the number of entries one level deeper listed after each of them.
	private static IReadOnlyList<(int Index, OutlineEntry Entry)> WithMemberCounts(IReadOnlyList<OutlineEntry> entries, int depth)
	{
		var selected = new List<(int Index, OutlineEntry Entry)>();
		for (var index = 0; index < entries.Count; index++)
		{
			var entry = entries[index];
			if (entry.Depth == depth)
				selected.Add((index, entry));
			else if (entry.Depth < depth)
				selected.Add((-1, entry));
			else if (entry.Depth == depth + 1 && selected.Count > 0 && selected[^1].Index >= 0)
				selected[^1] = selected[^1] with { Entry = selected[^1].Entry with { Members = selected[^1].Entry.Members + 1 } };
		}
		return selected.Where(static item => item.Index >= 0).ToArray();
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
		var members = entry.Members.ToString(CultureInfo.InvariantCulture) + (entry.Members == 1 ? " member" : " members");
		var suffix = (entry.Kind, entry.Members) switch
		{
			(null, 0) => string.Empty,
			(null, _) => $" ({members})",
			(_, 0) => $" ({entry.Kind})",
			_ => $" ({entry.Kind}, {members})"
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
			if (depth > MaximumNestingShown)
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
			if (depth > MaximumNestingShown)
				continue;
			entries.Add(new OutlineEntry(headings[index].Line, Math.Max(headings[index].Line, end), depth, headings[index].Title, null));
		}
		return entries;
	}

	private static int CountLines(string content)
	{
		if (content.Length == 0) return 0;
		var lines = 1;
		for (var index = 0; index < content.Length; index++)
		{
			if (content[index] == '\r')
			{
				lines++;
				if (index + 1 < content.Length && content[index + 1] == '\n') index++;
			}
			else if (content[index] == '\n') lines++;
		}
		return lines;
	}

	[GeneratedRegex("^\\s*\"(?<key>[^\"\\\\]{1,120})\"\\s*:", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
	private static partial Regex JsonKeyRegex();

	[GeneratedRegex("^(?<level>#{1,6})\\s+(?<title>\\S.{0,160})", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
	private static partial Regex MarkdownHeadingRegex();
}
