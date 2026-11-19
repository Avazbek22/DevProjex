namespace DevProjex.Mcp;

/// <summary>
/// Chooses the lines a search shows for a declaration whose body does not fit the body limit:
/// the signature up to the start of the body, plus the first docstring line in Python, never more
/// than <see cref="MaximumCharacters"/>. A fragment of a long body is read again in full anyway, so
/// the head only has to say what the declaration is and leave the exact range to the reader.
/// </summary>
internal static class McpDeclarationHead
{
	public const int MaximumCharacters = 400;

	private enum SignatureStyle
	{
		Unknown,
		Braces,
		Python
	}

	/// <summary>
	/// Returns how many of <paramref name="lines"/> form the head. The lines are the leading whole
	/// lines of the declaration that already fit the head budget; when no signature end is found
	/// among them, all of them are the head.
	/// </summary>
	public static int CountHeadLines(string relativePath, IReadOnlyList<string> lines)
	{
		ArgumentNullException.ThrowIfNull(lines);
		if (lines.Count == 0)
			return 0;
		var style = DetectStyle(relativePath);
		var signatureEnd = style switch
		{
			SignatureStyle.Braces => FindBraceSignatureEnd(lines, IsRust(relativePath)),
			SignatureStyle.Python => FindPythonSignatureEnd(lines),
			_ => -1
		};
		if (signatureEnd < 0)
			return lines.Count;
		var count = signatureEnd + 1;
		if (style == SignatureStyle.Python && count < lines.Count &&
			StartsDocstring(lines[count]))
		{
			count++;
		}
		return count;
	}

	private static SignatureStyle DetectStyle(string relativePath) =>
		Path.GetExtension(relativePath).ToLowerInvariant() switch
		{
			".py" or ".pyi" => SignatureStyle.Python,
			".cs" or ".ts" or ".mts" or ".cts" or ".tsx" or ".jsx" or ".js" or ".mjs" or ".cjs" or ".go" or
				".java" or ".rs" or ".kt" or ".kts" or ".php" or ".phtml" or ".c" or ".h" or ".cc" or ".cpp" or
				".cxx" or ".hh" or ".hpp" or ".hxx" or ".sh" or ".bash" or ".scala" or ".sc" => SignatureStyle.Braces,
			_ => SignatureStyle.Unknown
		};

	/// <summary>
	/// The body starts at the first '{' outside parentheses, brackets, strings and comments; a ';'
	/// there first means a declaration without a block body, which ends on that line.
	/// </summary>
	private static int FindBraceSignatureEnd(IReadOnlyList<string> lines, bool apostropheMayBeLifetime)
	{
		var depth = 0;
		var inBlockComment = false;
		for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
		{
			var line = lines[lineIndex];
			char? quote = null;
			for (var index = 0; index < line.Length; index++)
			{
				var character = line[index];
				var next = index + 1 < line.Length ? line[index + 1] : '\0';
				if (inBlockComment)
				{
					if (character == '*' && next == '/')
					{
						inBlockComment = false;
						index++;
					}
					continue;
				}
				if (quote is { } open)
				{
					if (character == '\\')
						index++;
					else if (character == open)
						quote = null;
					continue;
				}
				switch (character)
				{
					case '/' when next == '/':
						index = line.Length;
						break;
					case '/' when next == '*':
						inBlockComment = true;
						index++;
						break;
					case '"' or '`':
						quote = character;
						break;
					case '\'' when !apostropheMayBeLifetime || IsCharacterLiteral(line, index):
						quote = character;
						break;
					case '(' or '[':
						depth++;
						break;
					case ')' or ']':
						depth = Math.Max(0, depth - 1);
						break;
					case '{' or ';' when depth == 0:
						return lineIndex;
				}
			}
		}
		return -1;
	}

	private static bool IsRust(string relativePath) =>
		Path.GetExtension(relativePath).Equals(".rs", StringComparison.OrdinalIgnoreCase);

	// Rust lifetimes ('a) share the apostrophe with character literals; only a closed short literal is a quote.
	private static bool IsCharacterLiteral(string line, int index)
	{
		var close = line.IndexOf('\'', index + 1);
		return close > index && (close - index <= 3 || line[index + 1] == '\\');
	}

	/// <summary>
	/// A Python header ends on the first line that closes every bracket and ends with ':' once a
	/// def or class keyword has been seen; decorators before it belong to the head.
	/// </summary>
	private static int FindPythonSignatureEnd(IReadOnlyList<string> lines)
	{
		var depth = 0;
		var headerStarted = false;
		for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
		{
			var line = lines[lineIndex];
			var trimmed = line.TrimStart();
			if (!headerStarted && (depth > 0 || trimmed.StartsWith('@') || trimmed.Length == 0))
			{
				// Decorators, including ones whose arguments span lines, come before the header.
				StripPythonLine(line, ref depth);
				continue;
			}
			if (!headerStarted)
			{
				headerStarted = trimmed.StartsWith("def ", StringComparison.Ordinal) ||
					trimmed.StartsWith("async def ", StringComparison.Ordinal) ||
					trimmed.StartsWith("class ", StringComparison.Ordinal);
				if (!headerStarted)
					return -1;
			}

			var code = StripPythonLine(line, ref depth);
			if (depth == 0 && code.TrimEnd().EndsWith(':'))
				return lineIndex;
		}
		return -1;
	}

	private static string StripPythonLine(string line, ref int depth)
	{
		var code = new StringBuilder(line.Length);
		char? quote = null;
		for (var index = 0; index < line.Length; index++)
		{
			var character = line[index];
			if (quote is { } open)
			{
				if (character == '\\')
					index++;
				else if (character == open)
					quote = null;
				continue;
			}
			if (character == '#')
				break;
			if (character is '"' or '\'')
			{
				quote = character;
				continue;
			}
			if (character is '(' or '[' or '{')
				depth++;
			else if (character is ')' or ']' or '}')
				depth = Math.Max(0, depth - 1);
			code.Append(character);
		}
		return code.ToString();
	}

	private static bool StartsDocstring(string line)
	{
		var text = line.AsSpan().TrimStart();
		var prefix = 0;
		while (prefix < text.Length && prefix < 2 && "rRbBuUfF".Contains(text[prefix]))
			prefix++;
		text = text[prefix..];
		return text.StartsWith("\"\"\"", StringComparison.Ordinal) ||
			text.StartsWith("'''", StringComparison.Ordinal) ||
			text.Length > 0 && text[0] is '"' or '\'';
	}
}
