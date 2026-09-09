using System.Net;
using System.Text;
using System.Text.Json;
using DevProjex.Application.Secrets;

namespace DevProjex.Infrastructure.Secrets;

internal readonly record struct StructuredSecretValueSpan(int Start, int Length)
{
	public int End => checked(Start + Length);
}

internal static class StructuredSecretValueLexers
{
	private const int MaximumNestingDepth = 128;

	internal static IReadOnlyList<StructuredSecretValueSpan> FindDotEnvValues(
		ReadOnlySpan<char> content,
		SmartSecretStack stack,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var spans = new List<StructuredSecretValueSpan>();
		var lineStart = 0;
		while (lineStart < content.Length)
		{
			budget.Checkpoint(cancellationToken);
			var lineEnd = FindLineEnd(content, lineStart);
			var cursor = SkipHorizontalWhitespace(content, lineStart, lineEnd);
			if (cursor >= lineEnd || content[cursor] == '#')
			{
				lineStart = AdvancePastLineBreak(content, lineEnd);
				continue;
			}
			if (content[cursor..lineEnd].StartsWith("export", StringComparison.OrdinalIgnoreCase) &&
			    cursor + 6 < lineEnd && char.IsWhiteSpace(content[cursor + 6]))
			{
				cursor = SkipHorizontalWhitespace(content, cursor + 6, lineEnd);
			}

			var equals = content[cursor..lineEnd].IndexOf('=');
			if (equals <= 0)
			{
				lineStart = AdvancePastLineBreak(content, lineEnd);
				continue;
			}
			equals += cursor;
			var key = content[cursor..equals].Trim();
			if (!StructuredSecretDetector.IsSensitiveKey(key, stack))
			{
				lineStart = AdvancePastLineBreak(content, lineEnd);
				continue;
			}

			var value = ReadDotEnvValue(content, equals + 1);
			AddSpan(content, spans, value.Span);
			lineStart = AdvancePastLineBreak(content, FindLineEnd(content, value.ResumeAt));
		}
		return spans;
	}

	internal static IReadOnlyList<StructuredSecretValueSpan> FindNpmValues(
		ReadOnlySpan<char> content,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var spans = new List<StructuredSecretValueSpan>();
		var lineStart = 0;
		while (lineStart < content.Length)
		{
			budget.Checkpoint(cancellationToken);
			var lineEnd = FindLineEnd(content, lineStart);
			var cursor = SkipHorizontalWhitespace(content, lineStart, lineEnd);
			if (cursor >= lineEnd || content[cursor] is '#' or ';')
			{
				lineStart = AdvancePastLineBreak(content, lineEnd);
				continue;
			}
			var equals = content[cursor..lineEnd].IndexOf('=');
			if (equals <= 0)
			{
				lineStart = AdvancePastLineBreak(content, lineEnd);
				continue;
			}
			equals += cursor;
			var key = content[cursor..equals].Trim();
			var separator = key.LastIndexOf(':');
			if (separator >= 0)
				key = key[(separator + 1)..].Trim();
			if (!IsNpmCredentialKey(key))
			{
				lineStart = AdvancePastLineBreak(content, lineEnd);
				continue;
			}

			AddSpan(content, spans, ReadNpmValue(content, equals + 1, lineEnd));
			lineStart = AdvancePastLineBreak(content, lineEnd);
		}
		return spans;
	}

	internal static IReadOnlyList<StructuredSecretValueSpan> FindJsonValues(
		ReadOnlySpan<char> content,
		SmartSecretStack stack,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var spans = new List<StructuredSecretValueSpan>();
		var cursor = 0;
		SkipJsonTrivia(content, ref cursor);
		if (cursor >= content.Length || content[cursor] is not '{' and not '[')
			return spans;
		ParseJsonValue(content, ref cursor, inheritedSensitive: false, stack, spans, depth: 0, budget, cancellationToken);
		SkipJsonTrivia(content, ref cursor);
		if (cursor != content.Length)
			throw Incomplete("JSON");
		return spans;
	}

	internal static IReadOnlyList<StructuredSecretValueSpan> FindYamlValues(
		ReadOnlySpan<char> content,
		SmartSecretStack stack,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var spans = new List<StructuredSecretValueSpan>();
		var lineStart = 0;
		while (lineStart < content.Length)
		{
			budget.Checkpoint(cancellationToken);
			var lineEnd = FindLineEnd(content, lineStart);
			var first = SkipHorizontalWhitespace(content, lineStart, lineEnd);
			if (first >= lineEnd || content[first] == '#')
			{
				lineStart = AdvancePastLineBreak(content, lineEnd);
				continue;
			}
			if (content[first] == '-')
				first = SkipHorizontalWhitespace(content, first + 1, lineEnd);

			var delimiter = FindYamlKeyDelimiter(content, first, lineEnd);
			if (delimiter < 0)
			{
				lineStart = AdvancePastLineBreak(content, lineEnd);
				continue;
			}
			var key = DecodeYamlScalar(content[first..delimiter].Trim());
			if (!StructuredSecretDetector.IsSensitiveKey(key, stack))
			{
				lineStart = AdvancePastLineBreak(content, lineEnd);
				continue;
			}

			var valueStart = SkipHorizontalWhitespace(content, delimiter + 1, lineEnd);
			if (valueStart < lineEnd && content[valueStart] is '|' or '>')
			{
				lineStart = ReadYamlBlockScalar(
					content,
					lineStart,
					lineEnd,
					valueStart,
					spans);
				continue;
			}

			var value = ReadYamlInlineValue(content, valueStart, lineEnd);
			AddSpan(content, spans, value);
			lineStart = AdvancePastLineBreak(content, lineEnd);
		}
		return spans;
	}

	internal static IReadOnlyList<StructuredSecretValueSpan> FindPythonValues(
		ReadOnlySpan<char> content,
		SmartSecretStack stack,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var spans = new List<StructuredSecretValueSpan>();
		var lineStart = 0;
		while (lineStart < content.Length)
		{
			budget.Checkpoint(cancellationToken);
			var lineEnd = FindLineEnd(content, lineStart);
			var cursor = SkipHorizontalWhitespace(content, lineStart, lineEnd);
			if (cursor >= lineEnd || content[cursor] == '#')
			{
				lineStart = AdvancePastLineBreak(content, lineEnd);
				continue;
			}
			var keyStart = cursor;
			while (cursor < lineEnd && IsPythonIdentifierCharacter(content[cursor]))
				cursor++;
			if (cursor == keyStart || !StructuredSecretDetector.IsSensitiveKey(content[keyStart..cursor], stack))
			{
				lineStart = AdvancePastLineBreak(content, lineEnd);
				continue;
			}
			var equals = content[cursor..lineEnd].IndexOf('=');
			if (equals < 0)
			{
				lineStart = AdvancePastLineBreak(content, lineEnd);
				continue;
			}
			cursor += equals + 1;
			var resumeAt = lineEnd;
			var foundLiteral = false;
			while (true)
			{
				cursor = SkipHorizontalWhitespace(content, cursor, FindLineEnd(content, cursor));
				if (!TryReadPythonLiteral(content, cursor, out var literal, out resumeAt))
					break;
				AddSpan(content, spans, literal);
				foundLiteral = true;
				cursor = resumeAt;
			}
			if (!foundLiteral && TryReadPythonFallbackLiteral(content, cursor, out var fallback, out resumeAt))
				AddSpan(content, spans, fallback);
			lineStart = AdvancePastLineBreak(content, FindLineEnd(content, resumeAt));
		}
		return spans;
	}

	internal static IReadOnlyList<StructuredSecretValueSpan> FindDockerValues(
		ReadOnlySpan<char> content,
		SmartSecretStack stack,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var spans = new List<StructuredSecretValueSpan>();
		var escape = FindDockerEscapeDirective(content);
		var sourceStart = 0;
		while (sourceStart < content.Length)
		{
			budget.Checkpoint(cancellationToken);
			var logical = ReadDockerLogicalLine(content, sourceStart, escape);
			ParseDockerInstruction(logical, escape, stack, spans);
			sourceStart = logical.ResumeAt;
		}
		return spans;
	}

	internal static IReadOnlyList<StructuredSecretValueSpan> FindNetrcValues(
		ReadOnlySpan<char> content,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var spans = new List<StructuredSecretValueSpan>();
		var cursor = 0;
		while (TryReadNetrcToken(content, ref cursor, out var token))
		{
			budget.Checkpoint(cancellationToken);
			if (!content.Slice(token.Start, token.Length).Equals("password", StringComparison.OrdinalIgnoreCase))
				continue;
			if (TryReadNetrcToken(content, ref cursor, out var value))
				AddSpan(content, spans, value);
		}
		return spans;
	}

	internal static IReadOnlyList<StructuredSecretValueSpan> FindXmlValues(
		ReadOnlySpan<char> content,
		SmartSecretStack stack,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		var spans = new List<StructuredSecretValueSpan>();
		var elements = new Stack<XmlElementContext>();
		var cursor = 0;
		while (cursor < content.Length)
		{
			budget.Checkpoint(cancellationToken);
			if (content[cursor] != '<')
			{
				var textEnd = content[cursor..].IndexOf('<');
				textEnd = textEnd < 0 ? content.Length : cursor + textEnd;
				if (elements.TryPeek(out var element) && element.IsSensitive)
					AddSpan(content, spans, Trim(content, cursor, textEnd));
				cursor = textEnd;
				continue;
			}
			if (content[cursor..].StartsWith("<!--", StringComparison.Ordinal))
			{
				cursor = FindRequiredSuffix(content, cursor + 4, "-->", "XML");
				continue;
			}
			if (content[cursor..].StartsWith("<![CDATA[", StringComparison.Ordinal))
			{
				var valueStart = cursor + 9;
				var end = content[valueStart..].IndexOf("]]>", StringComparison.Ordinal);
				if (end < 0)
					throw Incomplete("XML");
				if (elements.TryPeek(out var element) && element.IsSensitive)
					AddSpan(content, spans, Trim(content, valueStart, valueStart + end));
				cursor = valueStart + end + 3;
				continue;
			}
			if (content[cursor..].StartsWith("<?", StringComparison.Ordinal))
			{
				cursor = FindRequiredSuffix(content, cursor + 2, "?>", "XML");
				continue;
			}
			if (content[cursor..].StartsWith("<!", StringComparison.Ordinal))
				throw new SecretDetectionException("Structured XML inspection does not permit declarations.");

			var tagEnd = FindXmlTagEnd(content, cursor + 1);
			if (tagEnd < 0)
				throw Incomplete("XML");
			var closing = cursor + 1 < content.Length && content[cursor + 1] == '/';
			if (closing)
			{
				if (elements.Count == 0)
					throw Incomplete("XML");
				elements.Pop();
				cursor = tagEnd + 1;
				continue;
			}

			var tag = content[(cursor + 1)..tagEnd];
			var tagCursor = 0;
			while (tagCursor < tag.Length && char.IsWhiteSpace(tag[tagCursor]))
				tagCursor++;
			var nameStart = tagCursor;
			while (tagCursor < tag.Length && IsXmlNameCharacter(tag[tagCursor]))
				tagCursor++;
			if (tagCursor == nameStart)
				throw Incomplete("XML");
			var elementName = LocalXmlName(tag[nameStart..tagCursor]);
			var attributes = ReadXmlAttributes(tag, cursor + 1, tagCursor);
			var inherited = elements.TryPeek(out var parent) && parent.IsSensitive;
			var elementSensitive = inherited || StructuredSecretDetector.IsSensitiveKey(elementName, stack);
			var keyAttributeSensitive = false;
			foreach (var attribute in attributes)
			{
				if ((attribute.Name.Equals("key", StringComparison.OrdinalIgnoreCase) ||
				     attribute.Name.Equals("name", StringComparison.OrdinalIgnoreCase)) &&
				    StructuredSecretDetector.IsSensitiveKey(
					    DecodeXml(content.Slice(attribute.Start, attribute.Length)),
					    stack))
				{
					keyAttributeSensitive = true;
					break;
				}
			}
			foreach (var attribute in attributes)
			{
				if (StructuredSecretDetector.IsSensitiveKey(attribute.Name, stack) ||
				    attribute.Name.Equals("value", StringComparison.OrdinalIgnoreCase) &&
				    (elementSensitive || keyAttributeSensitive))
				{
					AddSpan(content, spans, new StructuredSecretValueSpan(attribute.Start, attribute.Length));
				}
			}
			var selfClosing = tag.TrimEnd().EndsWith("/", StringComparison.Ordinal);
			if (!selfClosing)
				elements.Push(new XmlElementContext(elementSensitive || keyAttributeSensitive));
			cursor = tagEnd + 1;
		}
		if (elements.Count != 0)
			throw Incomplete("XML");
		return spans;
	}

	private static DotEnvValue ReadDotEnvValue(ReadOnlySpan<char> content, int start)
	{
		var lineEnd = FindLineEnd(content, start);
		start = SkipHorizontalWhitespace(content, start, lineEnd);
		if (start >= content.Length)
			return new DotEnvValue(new StructuredSecretValueSpan(start, 0), start);
		if (start < lineEnd && content[start] is '\'' or '"')
		{
			var quote = content[start++];
			var end = start;
			while (end < content.Length)
			{
				if (content[end] == quote && !IsEscaped(content, start, end))
					return new DotEnvValue(new StructuredSecretValueSpan(start, end - start), end + 1);
				end++;
			}
			throw Incomplete("dotenv");
		}

		var endOfValue = start;
		while (endOfValue < lineEnd)
		{
			if (content[endOfValue] == '#')
			{
				break;
			}
			endOfValue++;
		}
		return new DotEnvValue(Trim(content, start, endOfValue), endOfValue);
	}

	private static StructuredSecretValueSpan ReadNpmValue(ReadOnlySpan<char> content, int start, int lineEnd)
	{
		start = SkipHorizontalWhitespace(content, start, lineEnd);
		if (start >= lineEnd)
			return new StructuredSecretValueSpan(start, 0);
		if (content[start] is not '\'' and not '"')
			return Trim(content, start, lineEnd);
		var quote = content[start++];
		var cursor = start;
		while (cursor < lineEnd)
		{
			if (content[cursor] == quote && !IsEscaped(content, start, cursor))
				return new StructuredSecretValueSpan(start, cursor - start);
			cursor++;
		}
		throw Incomplete("npmrc");
	}

	private static void ParseJsonValue(
		ReadOnlySpan<char> content,
		ref int cursor,
		bool inheritedSensitive,
		SmartSecretStack stack,
		ICollection<StructuredSecretValueSpan> spans,
		int depth,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		budget.Checkpoint(cancellationToken);
		if (depth > MaximumNestingDepth || cursor >= content.Length)
			throw Incomplete("JSON");
		SkipJsonTrivia(content, ref cursor);
		if (cursor >= content.Length)
			throw Incomplete("JSON");
		switch (content[cursor])
		{
			case '{':
				ParseJsonObject(content, ref cursor, inheritedSensitive, stack, spans, depth + 1, budget, cancellationToken);
				return;
			case '[':
				ParseJsonArray(content, ref cursor, inheritedSensitive, stack, spans, depth + 1, budget, cancellationToken);
				return;
			case '"':
			{
				var value = ReadJsonString(content, ref cursor);
				if (inheritedSensitive)
					AddSpan(content, spans, value);
				return;
			}
			default:
			{
				var start = cursor;
				while (cursor < content.Length &&
				       !char.IsWhiteSpace(content[cursor]) &&
				       content[cursor] is not ',' and not ']' and not '}')
				{
					cursor++;
				}
				if (cursor == start)
					throw Incomplete("JSON");
				if (inheritedSensitive)
					AddSpan(content, spans, new StructuredSecretValueSpan(start, cursor - start));
				return;
			}
		}
	}

	private static void ParseJsonObject(
		ReadOnlySpan<char> content,
		ref int cursor,
		bool inheritedSensitive,
		SmartSecretStack stack,
		ICollection<StructuredSecretValueSpan> spans,
		int depth,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		cursor++;
		SkipJsonTrivia(content, ref cursor);
		if (cursor < content.Length && content[cursor] == '}')
		{
			cursor++;
			return;
		}
		while (cursor < content.Length)
		{
			budget.Checkpoint(cancellationToken);
			if (content[cursor] != '"')
				throw Incomplete("JSON");
			var property = ReadJsonString(content, ref cursor);
			var propertyName = DecodeJsonString(content.Slice(property.Start, property.Length));
			SkipJsonTrivia(content, ref cursor);
			if (cursor >= content.Length || content[cursor++] != ':')
				throw Incomplete("JSON");
			SkipJsonTrivia(content, ref cursor);
			var sensitive = inheritedSensitive || StructuredSecretDetector.IsSensitiveKey(propertyName, stack);
			ParseJsonValue(content, ref cursor, sensitive, stack, spans, depth, budget, cancellationToken);
			SkipJsonTrivia(content, ref cursor);
			if (cursor < content.Length && content[cursor] == ',')
			{
				cursor++;
				SkipJsonTrivia(content, ref cursor);
				if (cursor < content.Length && content[cursor] == '}')
				{
					cursor++;
					return;
				}
				continue;
			}
			if (cursor < content.Length && content[cursor] == '}')
			{
				cursor++;
				return;
			}
			throw Incomplete("JSON");
		}
		throw Incomplete("JSON");
	}

	private static void ParseJsonArray(
		ReadOnlySpan<char> content,
		ref int cursor,
		bool inheritedSensitive,
		SmartSecretStack stack,
		ICollection<StructuredSecretValueSpan> spans,
		int depth,
		SecretFileInspectionBudget budget,
		CancellationToken cancellationToken)
	{
		cursor++;
		SkipJsonTrivia(content, ref cursor);
		if (cursor < content.Length && content[cursor] == ']')
		{
			cursor++;
			return;
		}
		while (cursor < content.Length)
		{
			budget.Checkpoint(cancellationToken);
			ParseJsonValue(content, ref cursor, inheritedSensitive, stack, spans, depth, budget, cancellationToken);
			SkipJsonTrivia(content, ref cursor);
			if (cursor < content.Length && content[cursor] == ',')
			{
				cursor++;
				SkipJsonTrivia(content, ref cursor);
				if (cursor < content.Length && content[cursor] == ']')
				{
					cursor++;
					return;
				}
				continue;
			}
			if (cursor < content.Length && content[cursor] == ']')
			{
				cursor++;
				return;
			}
			throw Incomplete("JSON");
		}
		throw Incomplete("JSON");
	}

	private static StructuredSecretValueSpan ReadJsonString(ReadOnlySpan<char> content, ref int cursor)
	{
		if (cursor >= content.Length || content[cursor] != '"')
			throw Incomplete("JSON");
		var start = ++cursor;
		while (cursor < content.Length)
		{
			if (content[cursor] == '"')
			{
				var result = new StructuredSecretValueSpan(start, cursor - start);
				cursor++;
				return result;
			}
			if (content[cursor] == '\\')
			{
				cursor++;
				if (cursor >= content.Length)
					throw Incomplete("JSON");
				if (content[cursor] == 'u')
				{
					if (cursor > content.Length - 5)
						throw Incomplete("JSON");
					for (var index = cursor + 1; index <= cursor + 4; index++)
					{
						if (!Uri.IsHexDigit(content[index]))
							throw Incomplete("JSON");
					}
					cursor += 5;
					continue;
				}
				cursor++;
				continue;
			}
			if (content[cursor] is '\r' or '\n' || char.IsControl(content[cursor]))
				throw Incomplete("JSON");
			cursor++;
		}
		throw Incomplete("JSON");
	}

	private static string DecodeJsonString(ReadOnlySpan<char> raw)
	{
		if (raw.IndexOf('\\') < 0)
			return raw.ToString();
		try
		{
			return JsonSerializer.Deserialize<string>($"\"{raw.ToString()}\"") ?? string.Empty;
		}
		catch (JsonException exception)
		{
			throw new SecretDetectionException("Structured JSON inspection encountered incomplete syntax.", exception);
		}
	}

	private static void SkipJsonTrivia(ReadOnlySpan<char> content, ref int cursor)
	{
		while (cursor < content.Length)
		{
			if (char.IsWhiteSpace(content[cursor]))
			{
				cursor++;
				continue;
			}
			if (cursor + 1 < content.Length && content[cursor] == '/' && content[cursor + 1] == '/')
			{
				cursor = AdvancePastLineBreak(content, FindLineEnd(content, cursor + 2));
				continue;
			}
			if (cursor + 1 < content.Length && content[cursor] == '/' && content[cursor + 1] == '*')
			{
				var end = content[(cursor + 2)..].IndexOf("*/", StringComparison.Ordinal);
				if (end < 0)
					throw Incomplete("JSON");
				cursor += end + 4;
				continue;
			}
			break;
		}
	}

	private static int FindYamlKeyDelimiter(ReadOnlySpan<char> content, int start, int end)
	{
		var quote = '\0';
		for (var cursor = start; cursor < end; cursor++)
		{
			var character = content[cursor];
			if (quote != '\0')
			{
				if (character == quote)
				{
					if (quote == '\'' && cursor + 1 < end && content[cursor + 1] == '\'')
					{
						cursor++;
						continue;
					}
					if (!IsEscaped(content, start, cursor))
						quote = '\0';
				}
				continue;
			}
			if (character is '\'' or '"')
			{
				quote = character;
				continue;
			}
			if (character == ':' || character == '=')
				return cursor;
		}
		return -1;
	}

	private static string DecodeYamlScalar(ReadOnlySpan<char> value)
	{
		value = value.Trim();
		if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
			return value[1..^1].ToString().Replace("''", "'", StringComparison.Ordinal);
		if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
			return value[1..^1].ToString();
		return value.ToString();
	}

	private static StructuredSecretValueSpan ReadYamlInlineValue(
		ReadOnlySpan<char> content,
		int start,
		int lineEnd)
	{
		if (start >= lineEnd)
			return new StructuredSecretValueSpan(start, 0);
		if (content[start] == '\'')
		{
			var cursor = start + 1;
			while (cursor < lineEnd)
			{
				if (content[cursor] != '\'')
				{
					cursor++;
					continue;
				}
				if (cursor + 1 < lineEnd && content[cursor + 1] == '\'')
				{
					cursor += 2;
					continue;
				}
				return new StructuredSecretValueSpan(start + 1, cursor - start - 1);
			}
			throw Incomplete("YAML");
		}
		if (content[start] == '"')
		{
			var cursor = start + 1;
			while (cursor < lineEnd)
			{
				if (content[cursor] == '"' && !IsEscaped(content, start + 1, cursor))
					return new StructuredSecretValueSpan(start + 1, cursor - start - 1);
				cursor++;
			}
			throw Incomplete("YAML");
		}

		var end = start;
		while (end < lineEnd)
		{
			if (content[end] == '#' && end > start && char.IsWhiteSpace(content[end - 1]))
				break;
			end++;
		}
		return Trim(content, start, end);
	}

	private static int ReadYamlBlockScalar(
		ReadOnlySpan<char> content,
		int headerStart,
		int headerEnd,
		int indicatorStart,
		ICollection<StructuredSecretValueSpan> spans)
	{
		var parentIndent = CountIndent(content, headerStart, headerEnd);
		var explicitIndent = 0;
		for (var cursor = indicatorStart + 1; cursor < headerEnd && content[cursor] != '#'; cursor++)
		{
			if (content[cursor] is >= '1' and <= '9')
				explicitIndent = content[cursor] - '0';
		}
		var nextLine = AdvancePastLineBreak(content, headerEnd);
		var blockIndent = explicitIndent > 0 ? parentIndent + explicitIndent : -1;
		while (nextLine < content.Length)
		{
			var lineEnd = FindLineEnd(content, nextLine);
			var indent = CountIndent(content, nextLine, lineEnd);
			if (indent <= parentIndent && nextLine + indent < lineEnd)
				return nextLine;
			if (nextLine + indent < lineEnd)
			{
				if (blockIndent < 0)
					blockIndent = indent;
				if (indent < blockIndent)
					return nextLine;
				AddSpan(content, spans, new StructuredSecretValueSpan(nextLine + blockIndent, lineEnd - nextLine - blockIndent));
			}
			nextLine = AdvancePastLineBreak(content, lineEnd);
		}
		return content.Length;
	}

	private static bool TryReadPythonLiteral(
		ReadOnlySpan<char> content,
		int start,
		out StructuredSecretValueSpan span,
		out int resumeAt)
	{
		var cursor = start;
		while (cursor < content.Length && cursor - start < 2 && IsPythonPrefixCharacter(content[cursor]))
			cursor++;
		if (!IsPythonStringPrefix(content[start..cursor]))
		{
			span = default;
			resumeAt = start;
			return false;
		}
		if (cursor >= content.Length || content[cursor] is not '\'' and not '"')
		{
			span = default;
			resumeAt = start;
			return false;
		}
		var quote = content[cursor];
		var triple = cursor + 2 < content.Length && content[cursor + 1] == quote && content[cursor + 2] == quote;
		var delimiterLength = triple ? 3 : 1;
		var valueStart = cursor + delimiterLength;
		cursor = valueStart;
		while (cursor < content.Length)
		{
			if (!triple && content[cursor] is '\r' or '\n')
				throw Incomplete("Python");
			if (content[cursor] == quote && !IsEscaped(content, valueStart, cursor))
			{
				if (!triple || cursor + 2 < content.Length && content[cursor + 1] == quote && content[cursor + 2] == quote)
				{
					span = new StructuredSecretValueSpan(valueStart, cursor - valueStart);
					resumeAt = cursor + delimiterLength;
					return true;
				}
			}
			cursor++;
		}
		throw Incomplete("Python");
	}

	private static bool TryReadPythonFallbackLiteral(
		ReadOnlySpan<char> content,
		int start,
		out StructuredSecretValueSpan span,
		out int resumeAt)
	{
		var expression = content[start..];
		var functionLength = expression.StartsWith("os.getenv", StringComparison.Ordinal)
			? "os.getenv".Length
			: expression.StartsWith("os.environ.get", StringComparison.Ordinal)
				? "os.environ.get".Length
				: 0;
		if (functionLength == 0)
		{
			span = default;
			resumeAt = start;
			return false;
		}

		var cursor = SkipLogicalWhitespace(content, start + functionLength);
		if (cursor >= content.Length || content[cursor++] != '(')
		{
			span = default;
			resumeAt = start;
			return false;
		}
		var depth = 1;
		var quote = '\0';
		for (; cursor < content.Length && depth > 0; cursor++)
		{
			var character = content[cursor];
			if (quote != '\0')
			{
				if (character == quote && !IsEscaped(content, start, cursor))
					quote = '\0';
				continue;
			}
			if (character is '\'' or '"')
			{
				quote = character;
				continue;
			}
			if (character == '(')
				depth++;
			else if (character == ')')
				depth--;
			else if (character == ',' && depth == 1)
			{
				var literalStart = SkipLogicalWhitespace(content, cursor + 1);
				return TryReadPythonLiteral(content, literalStart, out span, out resumeAt);
			}
		}

		span = default;
		resumeAt = start;
		return false;
	}

	private static DockerLogicalLine ReadDockerLogicalLine(
		ReadOnlySpan<char> content,
		int sourceStart,
		char escape)
	{
		var text = new StringBuilder();
		var sourceMap = new List<int>();
		var lineStart = sourceStart;
		while (lineStart < content.Length)
		{
			var lineEnd = FindLineEnd(content, lineStart);
			var trimmedEnd = lineEnd;
			while (trimmedEnd > lineStart && content[trimmedEnd - 1] is ' ' or '\t')
				trimmedEnd--;
			var directive = content[lineStart..trimmedEnd].TrimStart();
			var continued = !directive.StartsWith("# escape=", StringComparison.OrdinalIgnoreCase) &&
			                trimmedEnd > lineStart && content[trimmedEnd - 1] == escape;
			var copyEnd = continued ? trimmedEnd - 1 : lineEnd;
			for (var cursor = lineStart; cursor < copyEnd; cursor++)
			{
				text.Append(content[cursor]);
				sourceMap.Add(cursor);
			}
			var resumeAt = AdvancePastLineBreak(content, lineEnd);
			if (!continued)
				return new DockerLogicalLine(text.ToString(), sourceMap.ToArray(), resumeAt);
			text.Append(' ');
			sourceMap.Add(-1);
			lineStart = resumeAt;
		}
		return new DockerLogicalLine(text.ToString(), sourceMap.ToArray(), content.Length);
	}

	private static void ParseDockerInstruction(
		DockerLogicalLine logical,
		char escape,
		SmartSecretStack stack,
		ICollection<StructuredSecretValueSpan> spans)
	{
		var text = logical.Text.AsSpan();
		var cursor = 0;
		while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
			cursor++;
		if (cursor >= text.Length || text[cursor] == '#')
			return;
		var directiveStart = cursor;
		while (cursor < text.Length && char.IsLetter(text[cursor]))
			cursor++;
		var directive = text[directiveStart..cursor];
		var isEnvironment = directive.Equals("ENV", StringComparison.OrdinalIgnoreCase);
		var isArgument = directive.Equals("ARG", StringComparison.OrdinalIgnoreCase);
		if (!isEnvironment && !isArgument)
			return;
		while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
			cursor++;
		var firstKeyStart = cursor;
		while (cursor < text.Length && IsEnvironmentKeyCharacter(text[cursor]))
			cursor++;
		if (cursor == firstKeyStart)
			return;
		if (cursor >= text.Length || text[cursor] != '=')
		{
			if (!isEnvironment)
				return;
			var valueStart = SkipLogicalWhitespace(text, cursor);
			if (StructuredSecretDetector.IsSensitiveKey(text[firstKeyStart..cursor], stack) &&
			    !StructuredSecretDetector.IsReferenceOrPlaceholder(text[valueStart..]))
				AddMappedSpans(logical, valueStart, text.Length, spans);
			return;
		}

		cursor = firstKeyStart;
		while (cursor < text.Length)
		{
			cursor = SkipLogicalWhitespace(text, cursor);
			var keyStart = cursor;
			while (cursor < text.Length && IsEnvironmentKeyCharacter(text[cursor]))
				cursor++;
			if (cursor == keyStart || cursor >= text.Length || text[cursor] != '=')
				break;
			var key = text[keyStart..cursor];
			var value = ReadDockerValue(text, cursor + 1, escape, out cursor);
			if (StructuredSecretDetector.IsSensitiveKey(key, stack) &&
			    !StructuredSecretDetector.IsReferenceOrPlaceholder(text[value.Start..value.End]))
				AddMappedSpans(logical, value.Start, value.End, spans);
		}
	}

	private static StructuredSecretValueSpan ReadDockerValue(
		ReadOnlySpan<char> text,
		int start,
		char escape,
		out int next)
	{
		start = SkipLogicalWhitespace(text, start);
		if (start >= text.Length)
		{
			next = start;
			return new StructuredSecretValueSpan(start, 0);
		}
		if (text[start] is '\'' or '"')
		{
			var quote = text[start++];
			var cursor = start;
			while (cursor < text.Length)
			{
				if (text[cursor] == quote && !IsEscapedBy(text, start, cursor, escape))
				{
					next = cursor + 1;
					return new StructuredSecretValueSpan(start, cursor - start);
				}
				cursor++;
			}
			throw Incomplete("Dockerfile");
		}

		var end = start;
		while (end < text.Length)
		{
			if (char.IsWhiteSpace(text[end]) && !IsEscapedBy(text, start, end, escape))
			{
				var candidate = SkipLogicalWhitespace(text, end);
				var keyEnd = candidate;
				while (keyEnd < text.Length && IsEnvironmentKeyCharacter(text[keyEnd]))
					keyEnd++;
				if (keyEnd > candidate && keyEnd < text.Length && text[keyEnd] == '=')
				{
					next = candidate;
					return new StructuredSecretValueSpan(start, end - start);
				}
			}
			end++;
		}
		next = end;
		return new StructuredSecretValueSpan(start, end - start);
	}

	private static void AddMappedSpans(
		DockerLogicalLine logical,
		int start,
		int end,
		ICollection<StructuredSecretValueSpan> spans)
	{
		var segmentStart = -1;
		var previous = -2;
		for (var cursor = start; cursor < end; cursor++)
		{
			var source = logical.SourceMap[cursor];
			if (source < 0)
			{
				if (segmentStart >= 0)
					AddBoundedSpan(spans, new StructuredSecretValueSpan(segmentStart, previous - segmentStart + 1));
				segmentStart = -1;
				previous = -2;
				continue;
			}
			if (segmentStart < 0)
				segmentStart = source;
			else if (source != previous + 1)
			{
				AddBoundedSpan(spans, new StructuredSecretValueSpan(segmentStart, previous - segmentStart + 1));
				segmentStart = source;
			}
			previous = source;
		}
		if (segmentStart >= 0)
			AddBoundedSpan(spans, new StructuredSecretValueSpan(segmentStart, previous - segmentStart + 1));
	}

	private static bool TryReadNetrcToken(
		ReadOnlySpan<char> content,
		ref int cursor,
		out StructuredSecretValueSpan token)
	{
		while (cursor < content.Length)
		{
			if (char.IsWhiteSpace(content[cursor]))
			{
				cursor++;
				continue;
			}
			if (content[cursor] != '#')
				break;
			cursor = AdvancePastLineBreak(content, FindLineEnd(content, cursor));
		}
		if (cursor >= content.Length)
		{
			token = default;
			return false;
		}
		if (content[cursor] is '\'' or '"')
		{
			var quote = content[cursor++];
			var start = cursor;
			while (cursor < content.Length)
			{
				if (content[cursor] == quote && !IsEscaped(content, start, cursor))
				{
					token = new StructuredSecretValueSpan(start, cursor - start);
					cursor++;
					return true;
				}
				cursor++;
			}
			throw Incomplete("netrc");
		}
		var tokenStart = cursor;
		while (cursor < content.Length && !char.IsWhiteSpace(content[cursor]) && content[cursor] != '#')
			cursor++;
		token = new StructuredSecretValueSpan(tokenStart, cursor - tokenStart);
		return token.Length > 0;
	}

	private static IReadOnlyList<XmlAttribute> ReadXmlAttributes(
		ReadOnlySpan<char> tag,
		int contentOffset,
		int cursor)
	{
		var attributes = new List<XmlAttribute>();
		while (cursor < tag.Length)
		{
			while (cursor < tag.Length && (char.IsWhiteSpace(tag[cursor]) || tag[cursor] == '/'))
				cursor++;
			var nameStart = cursor;
			while (cursor < tag.Length && IsXmlNameCharacter(tag[cursor]))
				cursor++;
			if (cursor == nameStart)
				break;
			var name = LocalXmlName(tag[nameStart..cursor]).ToString();
			while (cursor < tag.Length && char.IsWhiteSpace(tag[cursor]))
				cursor++;
			if (cursor >= tag.Length || tag[cursor] != '=')
				throw Incomplete("XML");
			cursor++;
			while (cursor < tag.Length && char.IsWhiteSpace(tag[cursor]))
				cursor++;
			if (cursor >= tag.Length || tag[cursor] is not '\'' and not '"')
				throw Incomplete("XML");
			var quote = tag[cursor++];
			var valueStart = cursor;
			while (cursor < tag.Length && tag[cursor] != quote)
				cursor++;
			if (cursor >= tag.Length)
				throw Incomplete("XML");
			attributes.Add(new XmlAttribute(name, contentOffset + valueStart, cursor - valueStart));
			cursor++;
		}
		return attributes;
	}

	private static int FindXmlTagEnd(ReadOnlySpan<char> content, int start)
	{
		var quote = '\0';
		for (var cursor = start; cursor < content.Length; cursor++)
		{
			if (quote != '\0')
			{
				if (content[cursor] == quote)
					quote = '\0';
				continue;
			}
			if (content[cursor] is '\'' or '"')
				quote = content[cursor];
			else if (content[cursor] == '>')
				return cursor;
		}
		return -1;
	}

	private static int FindRequiredSuffix(ReadOnlySpan<char> content, int start, string suffix, string format)
	{
		var offset = content[start..].IndexOf(suffix, StringComparison.Ordinal);
		if (offset < 0)
			throw Incomplete(format);
		return start + offset + suffix.Length;
	}

	private static string DecodeXml(ReadOnlySpan<char> value) => WebUtility.HtmlDecode(value.ToString());

	private static ReadOnlySpan<char> LocalXmlName(ReadOnlySpan<char> name)
	{
		var separator = name.LastIndexOf(':');
		return separator < 0 ? name : name[(separator + 1)..];
	}

	private static bool IsXmlNameCharacter(char character) =>
		char.IsLetterOrDigit(character) || character is '_' or '-' or ':' or '.';

	private static char FindDockerEscapeDirective(ReadOnlySpan<char> content)
	{
		var lineStart = 0;
		while (lineStart < content.Length)
		{
			var lineEnd = FindLineEnd(content, lineStart);
			var line = content[lineStart..lineEnd].Trim();
			if (line.IsEmpty)
			{
				lineStart = AdvancePastLineBreak(content, lineEnd);
				continue;
			}
			if (!line.StartsWith("#", StringComparison.Ordinal))
				break;
			line = line[1..].TrimStart();
			if (line.StartsWith("escape=", StringComparison.OrdinalIgnoreCase))
			{
				var value = line[7..].Trim();
				if (value.Length == 1 && value[0] is '\\' or '`')
					return value[0];
			}
			lineStart = AdvancePastLineBreak(content, lineEnd);
		}
		return '\\';
	}

	private static bool IsNpmCredentialKey(ReadOnlySpan<char> key) =>
		key.Equals("_auth", StringComparison.OrdinalIgnoreCase) ||
		key.Equals("_authToken", StringComparison.OrdinalIgnoreCase) ||
		key.Equals("_password", StringComparison.OrdinalIgnoreCase);

	private static void AddSpan(
		ReadOnlySpan<char> content,
		ICollection<StructuredSecretValueSpan> spans,
		StructuredSecretValueSpan span)
	{
		if (span.Length <= 0 || span.Start < 0 || span.Start > content.Length - span.Length ||
		    StructuredSecretDetector.IsReferenceOrPlaceholder(content.Slice(span.Start, span.Length)))
		{
			return;
		}
		AddBoundedSpan(spans, span);
	}

	private static void AddBoundedSpan(
		ICollection<StructuredSecretValueSpan> spans,
		StructuredSecretValueSpan span)
	{
		if (spans.Count >= SecretInspectionLimits.MaximumFindingsPerFile)
		{
			throw new SecretInspectionBudgetExceededException(
				nameof(SecretInspectionLimits.MaximumFindingsPerFile));
		}
		spans.Add(span);
	}

	private static bool IsPythonPrefixCharacter(char character) =>
		character is 'r' or 'R' or 'u' or 'U' or 'b' or 'B' or 'f' or 'F';

	private static bool IsPythonStringPrefix(ReadOnlySpan<char> prefix)
	{
		if (prefix.IsEmpty)
			return true;
		if (prefix.Length == 1)
			return IsPythonPrefixCharacter(prefix[0]);
		return prefix.Equals("br", StringComparison.OrdinalIgnoreCase) ||
		       prefix.Equals("rb", StringComparison.OrdinalIgnoreCase) ||
		       prefix.Equals("fr", StringComparison.OrdinalIgnoreCase) ||
		       prefix.Equals("rf", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsPythonIdentifierCharacter(char character) =>
		char.IsLetterOrDigit(character) || character == '_';

	private static bool IsEnvironmentKeyCharacter(char character) =>
		char.IsLetterOrDigit(character) || character == '_';

	private static int SkipLogicalWhitespace(ReadOnlySpan<char> content, int start)
	{
		while (start < content.Length && char.IsWhiteSpace(content[start]))
			start++;
		return start;
	}

	private static int SkipHorizontalWhitespace(ReadOnlySpan<char> content, int start, int end)
	{
		while (start < end && content[start] is ' ' or '\t')
			start++;
		return start;
	}

	private static int CountIndent(ReadOnlySpan<char> content, int start, int end)
	{
		var cursor = start;
		while (cursor < end && content[cursor] == ' ')
			cursor++;
		return cursor - start;
	}

	private static int FindLineEnd(ReadOnlySpan<char> content, int start)
	{
		if (start >= content.Length)
			return content.Length;
		var offset = content[start..].IndexOfAny('\r', '\n');
		return offset < 0 ? content.Length : start + offset;
	}

	private static int AdvancePastLineBreak(ReadOnlySpan<char> content, int lineEnd)
	{
		if (lineEnd >= content.Length)
			return content.Length;
		return content[lineEnd] == '\r' && lineEnd + 1 < content.Length && content[lineEnd + 1] == '\n'
			? lineEnd + 2
			: lineEnd + 1;
	}

	private static StructuredSecretValueSpan Trim(ReadOnlySpan<char> content, int start, int end)
	{
		while (start < end && char.IsWhiteSpace(content[start]))
			start++;
		while (end > start && char.IsWhiteSpace(content[end - 1]))
			end--;
		return new StructuredSecretValueSpan(start, end - start);
	}

	private static bool IsEscaped(ReadOnlySpan<char> content, int valueStart, int position) =>
		IsEscapedBy(content, valueStart, position, '\\');

	private static bool IsEscapedBy(
		ReadOnlySpan<char> content,
		int valueStart,
		int position,
		char escape)
	{
		var count = 0;
		for (var cursor = position - 1; cursor >= valueStart && content[cursor] == escape; cursor--)
			count++;
		return (count & 1) != 0;
	}

	private static SecretDetectionException Incomplete(string format) =>
		new($"Structured {format} inspection encountered incomplete syntax.");

	private readonly record struct DotEnvValue(StructuredSecretValueSpan Span, int ResumeAt);
	private readonly record struct DockerLogicalLine(string Text, int[] SourceMap, int ResumeAt);
	private readonly record struct XmlElementContext(bool IsSensitive);
	private readonly record struct XmlAttribute(string Name, int Start, int Length);
}
