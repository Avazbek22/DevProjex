namespace DevProjex.Infrastructure.Secrets;

internal static class SecretDetectionTextPolicy
{
	private static readonly string[] LiteralPlaceholderValues =
	[
		"changeme",
		"change_me",
		"change-me",
		"your-password-here",
		"your_password_here",
		"your-api-key-here",
		"your_api_key_here",
		"your-token-here",
		"your_token_here",
		"insert-key-here",
		"insert_key_here",
		"enter-password-here",
		"enter_password_here",
		"replace_me",
		"replaceme",
		"todo",
		"tbd",
		"placeholder",
		"n/a",
		"na",
		"null",
		"none",
		"unset"
	];

	internal static bool IsReferenceOrPlaceholder(ReadOnlySpan<char> value)
	{
		value = value.Trim();
		if (value.IsEmpty)
			return true;
		if (IsWrapped(value, "${", "}") ||
		    IsWrapped(value, "$(", ")") ||
		    IsWrapped(value, "{{", "}}") ||
		    IsWrapped(value, "<", ">") ||
		    value.Length > 2 && value[0] == '%' && value[^1] == '%')
		{
			return true;
		}

		foreach (var placeholder in LiteralPlaceholderValues)
		{
			if (value.Equals(placeholder, StringComparison.OrdinalIgnoreCase))
				return true;
		}

		if (value.Length < 4 || char.IsDigit(value[0]))
			return false;
		for (var index = 1; index < value.Length; index++)
		{
			if (value[index] != value[0])
				return false;
		}
		return true;
	}

	internal static bool IsReferenceOrPlaceholder(
		ReadOnlySpan<char> content,
		int start,
		int length)
	{
		if (length <= 0 || start < 0 || start > content.Length - length ||
		    IsReferenceOrPlaceholder(content.Slice(start, length)))
		{
			return true;
		}

		var wrapperStart = start;
		while (wrapperStart > 0 && char.IsWhiteSpace(content[wrapperStart - 1]))
			wrapperStart--;
		var wrapperEnd = start + length;
		while (wrapperEnd < content.Length && char.IsWhiteSpace(content[wrapperEnd]))
			wrapperEnd++;

		return content[start] == '%' && wrapperEnd < content.Length && content[wrapperEnd] == '%' ||
		       HasSurroundingWrapper(content, wrapperStart, wrapperEnd, "${", "}") ||
		       HasSurroundingWrapper(content, wrapperStart, wrapperEnd, "$(", ")") ||
		       HasSurroundingWrapper(content, wrapperStart, wrapperEnd, "{{", "}}") ||
		       HasSurroundingWrapper(content, wrapperStart, wrapperEnd, "<", ">") ||
		       HasSurroundingWrapper(content, wrapperStart, wrapperEnd, "%", "%");
	}

	internal static bool IsRfc2606DocumentationHost(ReadOnlySpan<char> host)
	{
		host = host.Trim();
		if (host.IsEmpty)
			return false;

		if (host[0] == '[')
		{
			var closingBracket = host.IndexOf(']');
			if (closingBracket <= 0)
				return false;
			var portSeparator = host.LastIndexOf(':');
			if (portSeparator > closingBracket)
				host = host[..portSeparator];
			if (host.Length <= 2 || host[^1] != ']')
				return false;
			host = host[1..^1];
		}
		else
		{
			var portSeparator = host.LastIndexOf(':');
			if (portSeparator >= 0)
				host = host[..portSeparator];
		}

		if (host.IsEmpty)
			return false;
		if (host[^1] == '.')
			host = host[..^1];
		if (host.IsEmpty)
			return false;
		if (IsHostOrSubdomainOf(host, "example.com") ||
		    IsHostOrSubdomainOf(host, "example.net") ||
		    IsHostOrSubdomainOf(host, "example.org"))
		{
			return true;
		}

		var lastLabelSeparator = host.LastIndexOf('.');
		if (lastLabelSeparator <= 0)
			return false;
		var lastLabel = host[(lastLabelSeparator + 1)..];
		return lastLabel.Equals("test", StringComparison.OrdinalIgnoreCase) ||
		       lastLabel.Equals("example", StringComparison.OrdinalIgnoreCase) ||
		       lastLabel.Equals("invalid", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsWrapped(ReadOnlySpan<char> value, string prefix, string suffix) =>
		value.Length > prefix.Length + suffix.Length &&
		value.StartsWith(prefix, StringComparison.Ordinal) &&
		value.EndsWith(suffix, StringComparison.Ordinal);

	private static bool HasSurroundingWrapper(
		ReadOnlySpan<char> content,
		int valueStart,
		int valueEnd,
		string prefix,
		string suffix) =>
		valueStart >= prefix.Length &&
		valueEnd <= content.Length - suffix.Length &&
		content.Slice(valueStart - prefix.Length, prefix.Length).SequenceEqual(prefix) &&
		content.Slice(valueEnd, suffix.Length).SequenceEqual(suffix);

	private static bool IsHostOrSubdomainOf(ReadOnlySpan<char> host, string domain) =>
		host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
		host.Length > domain.Length &&
		host[host.Length - domain.Length - 1] == '.' &&
		host.EndsWith(domain, StringComparison.OrdinalIgnoreCase);
}
