using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevProjex.Infrastructure.Compression;
using TreeSitter;

namespace DevProjex.Mcp;

internal sealed record McpTestBlockOutlineEntry(
	int StartLine, int EndLine, int StartIndex, int EndIndex, string Title, string Kind);

internal static class McpTestBlockOutlineExtractor
{
	private const int MaximumCachedResults = 8;
	private const int MaximumCachedEntries = 4_096;
	private static readonly object MemoGate = new();
	private static readonly Dictionary<MemoKey, IReadOnlyList<McpTestBlockOutlineEntry>> Memo = [];
	private static readonly Queue<MemoKey> MemoOrder = [];
	private readonly record struct MemoKey(string Grammar, string ProtectedTextHash);

	public static IReadOnlyList<McpTestBlockOutlineEntry> Extract(
		string relativePath, string protectedText, CancellationToken cancellationToken,
		IGrammarLibraryLocator? locator = null)
	{
		ArgumentNullException.ThrowIfNull(protectedText);
		cancellationToken.ThrowIfCancellationRequested();
		var grammar = Path.GetExtension(relativePath).ToLowerInvariant() switch
		{
			".js" or ".jsx" or ".mjs" or ".cjs" => "javascript",
			".ts" or ".mts" or ".cts" => "typescript",
			".tsx" => "tsx",
			_ => null
		};
		if (grammar is null || !ContainsPossibleRoot(protectedText, cancellationToken))
			return [];
		var ownsLocator = locator is null;
		var memoKey = ownsLocator
			? new MemoKey(grammar, Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(protectedText.AsSpan()))))
			: (MemoKey?)null;
		if (memoKey is { } key && TryReadMemo(key) is { } memoized)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return memoized;
		}
		try
		{
			locator ??= CodeCompressionFactory.CreateLocator();
			using var language = new Language(locator.Resolve("tree-sitter-" + grammar), "tree_sitter_" + grammar);
			using var parser = new Parser(language);
			using var tree = parser.Parse(protectedText);
			cancellationToken.ThrowIfCancellationRequested();
			if (tree is null || tree.RootNode.HasError)
				return [];
			using var statements = new Query(language, "(expression_statement) @statement");
			using var cursor = statements.Execute(tree.RootNode);
			var entries = new List<McpTestBlockOutlineEntry>();
			foreach (var capture in cursor.Captures)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (ReadStatement(capture.Node, protectedText) is { } entry)
					entries.Add(entry);
			}
			if (cursor.IsMatchLimitExceeded) return [];
			cancellationToken.ThrowIfCancellationRequested();
			var result = entries.AsReadOnly();
			return memoKey is { } completedKey ? Remember(completedKey, result) : result;
		}
		catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or
			BadImageFormatException or TypeInitializationException or IOException or UnauthorizedAccessException or
			InvalidOperationException or ArgumentException or NotSupportedException)
		{
			return [];
		}
		finally
		{
			if (ownsLocator) (locator as IDisposable)?.Dispose();
		}
	}

	private static IReadOnlyList<McpTestBlockOutlineEntry>? TryReadMemo(MemoKey key)
	{
		lock (MemoGate)
			return Memo.GetValueOrDefault(key);
	}

	private static IReadOnlyList<McpTestBlockOutlineEntry> Remember(
		MemoKey key, IReadOnlyList<McpTestBlockOutlineEntry> entries)
	{
		if (entries.Count > MaximumCachedEntries) return entries;
		lock (MemoGate)
		{
			if (Memo.TryGetValue(key, out var existing)) return existing;
			if (Memo.Count == MaximumCachedResults)
				Memo.Remove(MemoOrder.Dequeue());
			Memo.Add(key, entries);
			MemoOrder.Enqueue(key);
			return entries;
		}
	}

	private static McpTestBlockOutlineEntry? ReadStatement(Node statement, string source)
	{
		var call = UnwrapParentheses(statement.NamedChildren.FirstOrDefault());
		if (call?.Type != "call_expression" || ReadKind(call.GetChildForField("function")) is not { } kind)
			return null;
		var arguments = call.GetChildForField("arguments");
		if (arguments?.Type != "arguments" || arguments.NamedChildren.FirstOrDefault(static child => child.Type != "comment") is not { } title)
			return null;
		var endLine = checked((int)statement.EndPosition.Row + 1);
		if (statement.EndPosition.Column == 0 && statement.EndPosition.Row > statement.StartPosition.Row)
			endLine--;
		return new McpTestBlockOutlineEntry(
			checked((int)statement.StartPosition.Row + 1), endLine,
			checked((int)statement.StartIndex), checked((int)statement.EndIndex), ReadTitle(title, source), kind);
	}

	private static string? ReadKind(Node? callee)
	{
		var members = new List<string?>();
		while (callee is not null)
		{
			callee = UnwrapParentheses(callee);
			if (callee is null) return null;
			switch (callee.Type)
			{
				case "call_expression":
					callee = callee.GetChildForField("function");
					break;
				case "member_expression":
					members.Add(callee.GetChildForField("property")?.Text);
					callee = callee.GetChildForField("object");
					break;
				case "subscript_expression":
					members.Add(ReadStaticMember(callee.GetChildForField("index")));
					callee = callee.GetChildForField("object");
					break;
				case "identifier":
					return callee.Text switch
					{
						"describe" or "context" or "suite" => "describe",
						"it" or "specify" => "test",
						"test" => members.Contains("describe", StringComparer.Ordinal) ? "describe" : "test",
						"Deno" when members.LastOrDefault() == "test" => "test",
						_ => null
					};
				default:
					return null;
			}
		}
		return null;
	}

	private static Node? UnwrapParentheses(Node? node)
	{
		while (node?.Type == "parenthesized_expression") node = node.NamedChildren.FirstOrDefault(static child => child.Type != "comment");
		return node;
	}

	private static string? ReadStaticMember(Node? node)
	{
		if (node?.Type == "string")
			return node.Text.Length >= 2 ? node.Text[1..^1] : null;
		if (node?.Type == "template_string" && !node.NamedChildren.Any(static child => child.Type == "template_substitution"))
			return node.Text.Length >= 2 ? node.Text[1..^1] : null;
		return null;
	}

	private static string ReadTitle(Node node, string source)
	{
		var title = source.AsSpan(checked((int)node.StartIndex), checked((int)(node.EndIndex - node.StartIndex)));
		if (node.Type is "string" or "template_string" && title.Length >= 2)
			title = title[1..^1];
		var normalized = new StringBuilder(82);
		var characters = 0;
		var previousWhitespace = false;
		foreach (var rune in title.EnumerateRunes())
		{
			if (Rune.IsWhiteSpace(rune))
			{
				if (previousWhitespace) continue;
				normalized.Append(' ');
				previousWhitespace = true;
			}
			else
			{
				normalized.Append(rune);
				previousWhitespace = false;
			}
			if (++characters > 80) break;
		}
		var text = normalized.ToString();
		if (characters > 80)
			text = string.Concat(text.EnumerateRunes().Take(79).Select(static rune => rune.ToString())) + "…";
		return JsonSerializer.Serialize(text, McpToolResults.CompactJsonOptions);
	}

	private static bool ContainsPossibleRoot(string source, CancellationToken cancellationToken)
	{
		for (var index = 0; index < source.Length; index++)
		{
			if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
			if (!IdentifierPart(source[index])) continue;
			var start = index++;
			while (index < source.Length && IdentifierPart(source[index])) index++;
			var word = source.AsSpan(start, index - start);
			if (word.SequenceEqual("describe") || word.SequenceEqual("context") || word.SequenceEqual("suite") ||
				word.SequenceEqual("it") || word.SequenceEqual("test") || word.SequenceEqual("specify") || word.SequenceEqual("Deno"))
				return true;
		}
		return false;
	}

	private static bool IdentifierPart(char value) => char.IsLetterOrDigit(value) || value is '_' or '$';
}
