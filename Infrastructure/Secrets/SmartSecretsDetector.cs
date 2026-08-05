using System.Collections.Concurrent;
using System.Collections.Frozen;
using DevProjex.Application.Secrets;
using DevProjex.Application.Services;

namespace DevProjex.Infrastructure.Secrets;

/// <summary>
/// Combines provider-shaped Gitleaks rules with local, scope-aware configuration rules.
/// The structured tier deliberately favours recall: inside a recognised configuration shape,
/// a sensitive key is sufficient evidence even when the value is short or low-entropy.
/// </summary>
public sealed class SmartSecretsDetector(
	ISecretDetector providerDetector,
	SmartIgnoreService smartIgnore) : ISecretDetector
{
	internal const string StructuredRulesVersion = "smart-secrets-v1";

	public string RulesIdentity =>
		$"{providerDetector.RulesIdentity}:{StructuredRulesVersion}";

	public ISecretDetectionScope CreateScope(string projectRoot) =>
		new Scope(
			providerDetector.CreateScope(projectRoot),
			smartIgnore.CreateScopeResolver(projectRoot),
			RulesIdentity);

	public IReadOnlyList<DetectedSecret> Detect(
		string repositoryRelativePath,
		string content,
		CancellationToken cancellationToken = default) =>
		Detect(repositoryRelativePath, content.AsSpan(), cancellationToken);

	public IReadOnlyList<DetectedSecret> Detect(
		string repositoryRelativePath,
		ReadOnlySpan<char> content,
		CancellationToken cancellationToken = default) =>
		Combine(
			providerDetector.Detect(repositoryRelativePath, content, cancellationToken),
			StructuredSecretDetector.Detect(
				repositoryRelativePath,
				content,
				SmartSecretStack.None,
				cancellationToken));

	private static IReadOnlyList<DetectedSecret> Combine(
		IReadOnlyList<DetectedSecret> providerFindings,
		IReadOnlyList<DetectedSecret> structuredFindings)
	{
		if (providerFindings.Count == 0)
			return structuredFindings;
		if (structuredFindings.Count == 0)
			return providerFindings;

		var combined = new DetectedSecret[providerFindings.Count + structuredFindings.Count];
		for (var index = 0; index < structuredFindings.Count; index++)
			combined[index] = structuredFindings[index];
		for (var index = 0; index < providerFindings.Count; index++)
			combined[structuredFindings.Count + index] = providerFindings[index];
		return combined;
	}

	private sealed class Scope(
		ISecretDetectionScope providerScope,
		ISmartIgnoreScopeResolver scopeResolver,
		string rulesIdentity) : ISecretDetectionScope
	{
		private static readonly IReadOnlySet<string> AdditionalMarkerFiles =
			new[]
			{
				"compose.yml",
				"compose.yaml",
				"docker-compose.yml",
				"docker-compose.yaml"
			}.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

		private static readonly IReadOnlySet<string> AdditionalMarkerExtensions =
			new[] { ".tf" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

		private readonly ConcurrentDictionary<string, ScopeContext> _contextByDirectory =
			new(PathComparer.Default);
		private readonly ScopeContext _unscopedContext =
			new(SmartSecretStack.None, $"{rulesIdentity}:scope-0");

		public string GetRulesIdentity(string fullPath, string repositoryRelativePath) =>
			GetContext(fullPath, repositoryRelativePath).RulesIdentity;

		public IReadOnlyList<DetectedSecret> Detect(
			string fullPath,
			string repositoryRelativePath,
			ReadOnlySpan<char> content,
			CancellationToken cancellationToken = default)
		{
			var providerFindings = providerScope.Detect(
				fullPath,
				repositoryRelativePath,
				content,
				cancellationToken);
			var stack = GetContext(fullPath, repositoryRelativePath).Stack;
			var structuredFindings = StructuredSecretDetector.Detect(
				repositoryRelativePath,
				content,
				stack,
				cancellationToken);
			return Combine(providerFindings, structuredFindings);
		}

		private ScopeContext GetContext(string fullPath, string repositoryRelativePath)
		{
			// URI and connection-string probes are globally applicable and need no project
			// facts. Avoiding scope resolution for ordinary source files removes a filesystem
			// fixed cost from the overwhelmingly common path.
			if (!StructuredSecretDetector.UsesScopedVocabulary(repositoryRelativePath))
				return _unscopedContext;
			var directory = Path.GetDirectoryName(Path.GetFullPath(fullPath)) ?? string.Empty;
			return _contextByDirectory.GetOrAdd(directory, _ => ResolveContext(fullPath));
		}

		private ScopeContext ResolveContext(string fullPath)
		{
			var resolution = scopeResolver.ResolveFileOwningScope(
				fullPath,
				AdditionalMarkerFiles,
				AdditionalMarkerExtensions);
			var stack = SmartSecretStackResolver.Resolve(resolution.Facts);
			return new ScopeContext(stack, $"{rulesIdentity}:scope-{(int)stack}");
		}

		private readonly record struct ScopeContext(
			SmartSecretStack Stack,
			string RulesIdentity);
	}
}

[Flags]
internal enum SmartSecretStack
{
	None = 0,
	DotNet = 1 << 0,
	Node = 1 << 1,
	Python = 1 << 2,
	Jvm = 1 << 3,
	Terraform = 1 << 4,
	Container = 1 << 5
}

internal static class SmartSecretStackResolver
{
	private static readonly IReadOnlySet<string> TerraformExtensions =
		new[] { ".tf" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	private static readonly IReadOnlySet<string> DotNetExtensions =
		new[] { ".sln", ".csproj", ".fsproj", ".vbproj" }
			.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	private static readonly IReadOnlySet<string> NodeMarkers =
		new[] { "package.json", "package-lock.json", "pnpm-lock.yaml", "yarn.lock", "bun.lock", "bun.lockb" }
			.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	private static readonly IReadOnlySet<string> PythonMarkers =
		new[] { "pyproject.toml", "requirements.txt", "setup.py", "setup.cfg", "Pipfile", "poetry.lock" }
			.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	private static readonly IReadOnlySet<string> JvmMarkers =
		new[] { "pom.xml", "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts" }
			.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	private static readonly IReadOnlySet<string> ContainerMarkers =
		new[] { "compose.yml", "compose.yaml", "docker-compose.yml", "docker-compose.yaml" }
			.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

	public static SmartSecretStack Resolve(ProjectRootFacts facts)
	{
		var result = SmartSecretStack.None;
		if (facts.HasAnyFileExtension(DotNetExtensions))
			result |= SmartSecretStack.DotNet;
		if (facts.HasAnyMarkerFile(NodeMarkers))
			result |= SmartSecretStack.Node;
		if (facts.HasAnyMarkerFile(PythonMarkers))
			result |= SmartSecretStack.Python;
		if (facts.HasAnyMarkerFile(JvmMarkers))
			result |= SmartSecretStack.Jvm;
		if (facts.HasAnyFileExtension(TerraformExtensions))
			result |= SmartSecretStack.Terraform;
		if (facts.HasAnyMarkerFile(ContainerMarkers))
			result |= SmartSecretStack.Container;
		return result;
	}
}

internal static class StructuredSecretDetector
{
	private const int CredentialUriOrder = -400;
	private const int ConnectionPasswordOrder = -300;
	private const int ConfigurationValueOrder = -200;
	private const int EnvironmentValueOrder = -100;

	private static readonly string[] CredentialSchemes =
	[
		"postgres://",
		"postgresql://",
		"mysql://",
		"mongodb://",
		"mongodb+srv://",
		"redis://",
		"rediss://",
		"amqp://",
		"amqps://",
		"http://",
		"https://"
	];

	public static IReadOnlyList<DetectedSecret> Detect(
		string repositoryRelativePath,
		ReadOnlySpan<char> content,
		SmartSecretStack stack,
		CancellationToken cancellationToken)
	{
		if (content.IsEmpty)
			return [];

		var findings = new List<DetectedSecret>();
		DetectCredentialUris(content, findings, cancellationToken);
		DetectConnectionStrings(content, findings, cancellationToken);

		var fileKind = ClassifyFile(repositoryRelativePath);
		if (fileKind == StructuredSecretFileKind.Environment)
			DetectEnvironmentAssignments(content, stack, findings, cancellationToken);
		else if (fileKind != StructuredSecretFileKind.None)
			DetectConfigurationValues(content, fileKind, stack, findings, cancellationToken);

		return findings;
	}

	internal static bool UsesScopedVocabulary(string repositoryRelativePath) =>
		ClassifyFile(repositoryRelativePath) != StructuredSecretFileKind.None;

	private static void DetectCredentialUris(
		ReadOnlySpan<char> content,
		ICollection<DetectedSecret> findings,
		CancellationToken cancellationToken)
	{
		for (var schemeIndex = 0; schemeIndex < CredentialSchemes.Length; schemeIndex++)
		{
			var scheme = CredentialSchemes[schemeIndex];
			var searchStart = 0;
			while (searchStart <= content.Length - scheme.Length)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var relativeSchemeStart = content[searchStart..].IndexOf(
					scheme.AsSpan(),
					StringComparison.OrdinalIgnoreCase);
				if (relativeSchemeStart < 0)
					break;

				var authorityStart = searchStart + relativeSchemeStart + scheme.Length;
				var authorityEnd = FindUriAuthorityEnd(content, authorityStart);
				var authority = content[authorityStart..authorityEnd];
				var at = authority.LastIndexOf('@');
				if (at > 0)
				{
					var colon = authority[..at].IndexOf(':');
					if (colon >= 0)
					{
						var valueStart = authorityStart + colon + 1;
						AddFinding(
							content,
							valueStart,
							authorityStart + at - valueStart,
							"credential-uri-password",
							CredentialUriOrder,
							findings);
					}
				}

				searchStart = Math.Max(authorityStart, authorityEnd);
			}
		}
	}

	private static int FindUriAuthorityEnd(ReadOnlySpan<char> content, int start)
	{
		var end = start;
		var inTemplate = false;
		while (end < content.Length)
		{
			var character = content[end];
			if (!inTemplate && end + 1 < content.Length &&
			    character == '{' && content[end + 1] == '{')
			{
				inTemplate = true;
				end += 2;
				continue;
			}
			if (inTemplate && end + 1 < content.Length &&
			    character == '}' && content[end + 1] == '}')
			{
				inTemplate = false;
				end += 2;
				continue;
			}
			if (!inTemplate &&
			    (character is '/' or '?' or '#' or '\'' or '"' or '<' or '>' || char.IsWhiteSpace(character)))
				break;
			end++;
		}
		return end;
	}

	private static void DetectConnectionStrings(
		ReadOnlySpan<char> content,
		ICollection<DetectedSecret> findings,
		CancellationToken cancellationToken)
	{
		var nextLineStart = 0;
		while (TryReadLine(content, ref nextLineStart, out var line, out var lineStart))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!LooksLikeConnectionString(line))
				continue;

			var position = 0;
			while (position < line.Length)
			{
				var equals = line[position..].IndexOf('=');
				if (equals < 0)
					break;
				equals += position;
				var keyStart = equals - 1;
				while (keyStart >= 0 && IsConnectionKeyCharacter(line[keyStart]))
					keyStart--;
				keyStart++;
				var key = line[keyStart..equals].Trim();
				if (key.Equals("password", StringComparison.OrdinalIgnoreCase) ||
				    key.Equals("pwd", StringComparison.OrdinalIgnoreCase))
				{
					var value = FindDelimitedValue(line, equals + 1, ';', '&');
					AddFinding(
						content,
						lineStart + value.Start,
						value.Length,
						"connection-password",
						ConnectionPasswordOrder,
						findings);
					position = Math.Max(equals + 1, value.End + 1);
					continue;
				}

				position = equals + 1;
			}
		}
	}

	private static bool LooksLikeConnectionString(ReadOnlySpan<char> line)
	{
		var assignmentCount = 0;
		var hasConnectionSignal = Contains(line, "jdbc:");
		var position = 0;
		while (position < line.Length)
		{
			var relativeEquals = line[position..].IndexOf('=');
			if (relativeEquals < 0)
				break;
			var equals = position + relativeEquals;
			assignmentCount++;
			var keyStart = equals - 1;
			while (keyStart >= 0 && IsConnectionKeyCharacter(line[keyStart]))
				keyStart--;
			var key = line[(keyStart + 1)..equals].Trim();
			hasConnectionSignal |=
				key.Equals("host", StringComparison.OrdinalIgnoreCase) ||
				key.Equals("server", StringComparison.OrdinalIgnoreCase) ||
				key.Equals("data source", StringComparison.OrdinalIgnoreCase) ||
				key.Equals("database", StringComparison.OrdinalIgnoreCase) ||
				key.Equals("initial catalog", StringComparison.OrdinalIgnoreCase) ||
				key.Equals("user id", StringComparison.OrdinalIgnoreCase) ||
				key.Equals("username", StringComparison.OrdinalIgnoreCase);
			position = equals + 1;
		}
		return assignmentCount >= 2 && hasConnectionSignal;
	}

	private static void DetectEnvironmentAssignments(
		ReadOnlySpan<char> content,
		SmartSecretStack stack,
		ICollection<DetectedSecret> findings,
		CancellationToken cancellationToken)
	{
		var nextLineStart = 0;
		while (TryReadLine(content, ref nextLineStart, out var line, out var lineStart))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var trimmedOffset = IndexOfFirstNonWhitespace(line);
			if (trimmedOffset < 0 || line[trimmedOffset] == '#')
				continue;
			if (line[trimmedOffset..].StartsWith("export ", StringComparison.OrdinalIgnoreCase))
				trimmedOffset += "export ".Length;

			var equals = line[trimmedOffset..].IndexOf('=');
			if (equals <= 0)
				continue;
			equals += trimmedOffset;
			var key = line[trimmedOffset..equals].Trim();
			if (!IsSensitiveKey(key, stack))
				continue;

			var value = FindEnvironmentValue(line, equals + 1);
			AddFinding(
				content,
				lineStart + value.Start,
				value.Length,
				"environment-secret",
				EnvironmentValueOrder,
				findings);
		}
	}

	private static void DetectConfigurationValues(
		ReadOnlySpan<char> content,
		StructuredSecretFileKind fileKind,
		SmartSecretStack stack,
		ICollection<DetectedSecret> findings,
		CancellationToken cancellationToken)
	{
		if (fileKind == StructuredSecretFileKind.Xml)
		{
			DetectXmlConfigurationValues(content, stack, findings, cancellationToken);
			return;
		}

		var nextLineStart = 0;
		while (TryReadLine(content, ref nextLineStart, out var line, out var lineStart))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var position = 0;
			while (position < line.Length)
			{
				if (!TryFindKeyValue(line, position, fileKind, out var key, out var value))
					break;
				if (!IsSensitiveKey(key, stack))
				{
					// An object-valued key can contain another key/value pair on the same line.
					// Continue at the value start instead of skipping the entire object.
					position = Math.Max(value.Start, position + 1);
					continue;
				}

				AddFinding(
					content,
					lineStart + value.Start,
					value.Length,
					"config-secret",
					ConfigurationValueOrder,
					findings);
				position = Math.Max(value.End + 1, position + 1);
			}
		}
	}

	private static void DetectXmlConfigurationValues(
		ReadOnlySpan<char> content,
		SmartSecretStack stack,
		ICollection<DetectedSecret> findings,
		CancellationToken cancellationToken)
	{
		var searchStart = 0;
		while (searchStart < content.Length)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var relativeTagStart = content[searchStart..].IndexOf('<');
			if (relativeTagStart < 0)
				break;
			var tagStart = searchStart + relativeTagStart;
			var tagEnd = FindXmlTagEnd(content, tagStart + 1);
			if (tagEnd < 0)
				break;

			var tag = content[(tagStart + 1)..tagEnd];
			var cursor = IndexOfFirstNonWhitespace(tag);
			if (cursor < 0 || tag[cursor] is '/' or '!' or '?')
			{
				searchStart = tagEnd + 1;
				continue;
			}

			var elementNameStart = cursor;
			while (cursor < tag.Length && IsXmlNameCharacter(tag[cursor]))
				cursor++;
			var elementName = tag[elementNameStart..cursor];
			var attributes = ParseXmlAttributes(tag, tagStart + 1, cursor);
			var elementIsSensitive = IsSensitiveKey(elementName, stack);
			var keyAttributeIsSensitive = false;
			foreach (var attribute in attributes)
			{
				if ((attribute.Name.Equals("key", StringComparison.OrdinalIgnoreCase) ||
				     attribute.Name.Equals("name", StringComparison.OrdinalIgnoreCase)) &&
				    IsSensitiveKey(content.Slice(attribute.ValueStart, attribute.ValueLength), stack))
				{
					keyAttributeIsSensitive = true;
					break;
				}
			}

			foreach (var attribute in attributes)
			{
				var attributeIsSensitive = IsSensitiveKey(attribute.Name, stack) ||
				                           (attribute.Name.Equals("value", StringComparison.OrdinalIgnoreCase) &&
				                            (elementIsSensitive || keyAttributeIsSensitive));
				if (!attributeIsSensitive)
					continue;
				AddFinding(
					content,
					attribute.ValueStart,
					attribute.ValueLength,
					"config-secret",
					ConfigurationValueOrder,
					findings);
			}

			if (elementIsSensitive)
			{
				var textStart = tagEnd + 1;
				var relativeTextEnd = content[textStart..].IndexOf('<');
				if (relativeTextEnd >= 0)
				{
					var textEnd = textStart + relativeTextEnd;
					while (textStart < textEnd && char.IsWhiteSpace(content[textStart]))
						textStart++;
					while (textEnd > textStart && char.IsWhiteSpace(content[textEnd - 1]))
						textEnd--;
					AddFinding(
						content,
						textStart,
						textEnd - textStart,
						"config-secret",
						ConfigurationValueOrder,
						findings);
				}
			}

			searchStart = tagEnd + 1;
		}
	}

	private static int FindXmlTagEnd(ReadOnlySpan<char> content, int start)
	{
		var quote = '\0';
		for (var index = start; index < content.Length; index++)
		{
			var character = content[index];
			if (quote != '\0')
			{
				if (character == quote)
					quote = '\0';
				continue;
			}
			if (character is '\'' or '"')
				quote = character;
			else if (character == '>')
				return index;
		}
		return -1;
	}

	private static IReadOnlyList<XmlAttributeSpan> ParseXmlAttributes(
		ReadOnlySpan<char> tag,
		int contentOffset,
		int start)
	{
		var attributes = new List<XmlAttributeSpan>();
		var cursor = start;
		while (cursor < tag.Length)
		{
			while (cursor < tag.Length && (char.IsWhiteSpace(tag[cursor]) || tag[cursor] == '/'))
				cursor++;
			var nameStart = cursor;
			while (cursor < tag.Length && IsXmlNameCharacter(tag[cursor]))
				cursor++;
			if (cursor == nameStart)
				break;
			var name = tag[nameStart..cursor].ToString();
			while (cursor < tag.Length && char.IsWhiteSpace(tag[cursor]))
				cursor++;
			if (cursor >= tag.Length || tag[cursor] != '=')
				continue;
			cursor++;
			while (cursor < tag.Length && char.IsWhiteSpace(tag[cursor]))
				cursor++;
			if (cursor >= tag.Length)
				break;

			var quote = tag[cursor] is '\'' or '"' ? tag[cursor++] : '\0';
			var valueStart = cursor;
			if (quote == '\0')
			{
				while (cursor < tag.Length && !char.IsWhiteSpace(tag[cursor]) && tag[cursor] != '/')
					cursor++;
			}
			else
			{
				while (cursor < tag.Length && tag[cursor] != quote)
					cursor++;
			}
			attributes.Add(new XmlAttributeSpan(
				name,
				contentOffset + valueStart,
				cursor - valueStart));
			if (cursor < tag.Length && quote != '\0')
				cursor++;
		}
		return attributes;
	}

	private static bool TryFindKeyValue(
		ReadOnlySpan<char> line,
		int searchStart,
		StructuredSecretFileKind fileKind,
		out ReadOnlySpan<char> key,
		out TextSpan value)
	{
		for (var delimiter = searchStart; delimiter < line.Length; delimiter++)
		{
			if (line[delimiter] != '=' && line[delimiter] != ':')
				continue;
			if (line[delimiter] == ':' && delimiter + 2 < line.Length && line[delimiter + 1] == '/' && line[delimiter + 2] == '/')
				continue;

			var keyEnd = delimiter;
			while (keyEnd > searchStart && char.IsWhiteSpace(line[keyEnd - 1]))
				keyEnd--;
			var keyStart = keyEnd - 1;
			if (keyStart >= searchStart && line[keyStart] is '\'' or '"')
			{
				var quote = line[keyStart--];
				while (keyStart >= searchStart && line[keyStart] != quote)
					keyStart--;
				keyStart++;
			}
			else
			{
				while (keyStart >= searchStart && IsConfigKeyCharacter(line[keyStart]))
					keyStart--;
				keyStart++;
			}

			if (keyStart >= keyEnd)
				continue;
			key = TrimKey(line[keyStart..keyEnd]);
			value = fileKind switch
			{
				StructuredSecretFileKind.Xml => FindDelimitedValue(line, delimiter + 1, ' ', '>'),
				StructuredSecretFileKind.Python => FindPythonLiteralValue(line, delimiter + 1),
				_ => FindConfigurationValue(line, delimiter + 1)
			};
			return true;
		}

		key = default;
		value = default;
		return false;
	}

	private static TextSpan FindPythonLiteralValue(ReadOnlySpan<char> line, int start)
	{
		while (start < line.Length && char.IsWhiteSpace(line[start]))
			start++;
		// settings.py is executable source, even though it conventionally carries
		// configuration. Only string literals are values; environment lookups and other
		// expressions must remain visible as references rather than being redacted as data.
		return start < line.Length && line[start] is '\'' or '"'
			? FindConfigurationValue(line, start)
			: new TextSpan(start, 0);
	}

	private static TextSpan FindEnvironmentValue(ReadOnlySpan<char> line, int start)
	{
		var value = FindConfigurationValue(line, start);
		if (value.Length == 0)
			return value;
		if (line[value.Start] is '\'' or '"')
			return value;

		var valueSpan = line.Slice(value.Start, value.Length);
		for (var index = 1; index < valueSpan.Length; index++)
		{
			if (valueSpan[index] == '#' && char.IsWhiteSpace(valueSpan[index - 1]))
				return TrimEnd(line, value.Start, value.Start + index);
		}
		return value;
	}

	private static TextSpan FindConfigurationValue(ReadOnlySpan<char> line, int start)
	{
		while (start < line.Length && char.IsWhiteSpace(line[start]))
			start++;
		if (start >= line.Length)
			return new TextSpan(start, 0);
		if (TryFindReferenceEnd(line, start, out var referenceEnd))
			return new TextSpan(start, referenceEnd - start);

		if (line[start] is '\'' or '"')
		{
			var quote = line[start++];
			var end = start;
			while (end < line.Length)
			{
				if (line[end] == quote && (end == start || line[end - 1] != '\\'))
					break;
				end++;
			}
			return new TextSpan(start, end - start);
		}

		var unquotedEnd = start;
		while (unquotedEnd < line.Length && line[unquotedEnd] is not ',' and not '}' and not ']' and not '#')
			unquotedEnd++;
		return TrimEnd(line, start, unquotedEnd);
	}

	private static bool TryFindReferenceEnd(ReadOnlySpan<char> line, int start, out int end)
	{
		if (line[start..].StartsWith("${", StringComparison.Ordinal))
			return TryFindSuffix(line, start + 2, "}", out end);
		if (line[start..].StartsWith("$(", StringComparison.Ordinal))
			return TryFindSuffix(line, start + 2, ")", out end);
		if (line[start..].StartsWith("{{", StringComparison.Ordinal))
			return TryFindSuffix(line, start + 2, "}}", out end);
		if (line[start] == '<')
			return TryFindSuffix(line, start + 1, ">", out end);
		if (line[start] == '%')
			return TryFindSuffix(line, start + 1, "%", out end);
		end = 0;
		return false;
	}

	private static bool TryFindSuffix(
		ReadOnlySpan<char> line,
		int searchStart,
		string suffix,
		out int end)
	{
		var relativeEnd = line[searchStart..].IndexOf(suffix, StringComparison.Ordinal);
		if (relativeEnd < 0)
		{
			end = 0;
			return false;
		}
		end = searchStart + relativeEnd + suffix.Length;
		return true;
	}

	private static TextSpan FindDelimitedValue(
		ReadOnlySpan<char> line,
		int start,
		char firstDelimiter,
		char secondDelimiter)
	{
		while (start < line.Length && char.IsWhiteSpace(line[start]))
			start++;
		var quote = start < line.Length && line[start] is '\'' or '"' ? line[start++] : '\0';
		var end = start;
		while (end < line.Length)
		{
			if (quote != '\0' && line[end] == quote)
				break;
			if (quote == '\0' && (line[end] == firstDelimiter || line[end] == secondDelimiter || line[end] is '\'' or '"'))
				break;
			end++;
		}
		return TrimEnd(line, start, end);
	}

	private static TextSpan TrimEnd(ReadOnlySpan<char> line, int start, int end)
	{
		while (end > start && char.IsWhiteSpace(line[end - 1]))
			end--;
		return new TextSpan(start, end - start);
	}

	private static void AddFinding(
		ReadOnlySpan<char> content,
		int start,
		int length,
		string ruleId,
		int ruleOrder,
		ICollection<DetectedSecret> findings)
	{
		if (length <= 0 || start < 0 || start > content.Length - length)
			return;
		var value = content.Slice(start, length);
		if (IsReferenceOrPlaceholder(value))
			return;
		findings.Add(new DetectedSecret(ruleId, start, length, value.ToString(), ruleOrder));
	}

	internal static bool IsReferenceOrPlaceholder(ReadOnlySpan<char> value)
	{
		value = value.Trim();
		if (value.IsEmpty)
			return true;
		return IsWrapped(value, "${", "}") ||
		       IsWrapped(value, "$(", ")") ||
		       IsWrapped(value, "{{", "}}") ||
		       IsWrapped(value, "<", ">") ||
		       value.Length > 2 && value[0] == '%' && value[^1] == '%';
	}

	private static bool IsWrapped(ReadOnlySpan<char> value, string prefix, string suffix) =>
		value.Length > prefix.Length + suffix.Length &&
		value.StartsWith(prefix, StringComparison.Ordinal) &&
		value.EndsWith(suffix, StringComparison.Ordinal);

	private static bool IsSensitiveKey(ReadOnlySpan<char> key, SmartSecretStack stack)
	{
		Span<char> normalizedBuffer = stackalloc char[Math.Min(key.Length, 128)];
		var length = 0;
		for (var index = 0; index < key.Length && length < normalizedBuffer.Length; index++)
		{
			if (char.IsLetterOrDigit(key[index]))
				normalizedBuffer[length++] = char.ToLowerInvariant(key[index]);
		}
		var normalized = normalizedBuffer[..length];
		if (normalized.IsEmpty)
			return false;

		if (Contains(normalized, "password") ||
		    normalized.Equals("passwd", StringComparison.Ordinal) ||
		    normalized.Equals("pwd", StringComparison.Ordinal) ||
		    Contains(normalized, "secret") ||
		    normalized.EndsWith("token", StringComparison.Ordinal) ||
		    Contains(normalized, "apikey") ||
		    Contains(normalized, "accesskey") ||
		    Contains(normalized, "privatekey") ||
		    Contains(normalized, "signingkey") ||
		    Contains(normalized, "credential"))
		{
			return true;
		}

		return stack.HasFlag(SmartSecretStack.DotNet) && normalized.Equals("jwtkey", StringComparison.Ordinal) ||
		       stack.HasFlag(SmartSecretStack.Node) && normalized.Equals("npmauth", StringComparison.Ordinal) ||
		       stack.HasFlag(SmartSecretStack.Python) && normalized.Equals("djangokey", StringComparison.Ordinal) ||
		       stack.HasFlag(SmartSecretStack.Terraform) && normalized.Equals("terraformauth", StringComparison.Ordinal) ||
		       stack.HasFlag(SmartSecretStack.Container) && normalized.Equals("registryauth", StringComparison.Ordinal);
	}

	private static StructuredSecretFileKind ClassifyFile(string path)
	{
		var fileName = Path.GetFileName(path);
		if (fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase))
			return StructuredSecretFileKind.Environment;
		if (fileName.Equals(".npmrc", StringComparison.OrdinalIgnoreCase))
			return StructuredSecretFileKind.Environment;
		if (fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) &&
		    fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
			return StructuredSecretFileKind.Configuration;
		if (fileName.EndsWith(".config", StringComparison.OrdinalIgnoreCase))
			return StructuredSecretFileKind.Xml;
		if ((fileName.StartsWith("application", StringComparison.OrdinalIgnoreCase) ||
		     fileName.StartsWith("docker-compose", StringComparison.OrdinalIgnoreCase) ||
		     fileName.StartsWith("compose.", StringComparison.OrdinalIgnoreCase)) &&
		    (fileName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
		     fileName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)))
			return StructuredSecretFileKind.Configuration;
		if (fileName.EndsWith(".tfvars", StringComparison.OrdinalIgnoreCase) ||
		    fileName.EndsWith(".tfvars.json", StringComparison.OrdinalIgnoreCase))
			return StructuredSecretFileKind.Configuration;
		if (fileName.Equals("settings.py", StringComparison.OrdinalIgnoreCase))
			return StructuredSecretFileKind.Python;
		return StructuredSecretFileKind.None;
	}

	private static bool TryReadLine(
		ReadOnlySpan<char> content,
		ref int nextLineStart,
		out ReadOnlySpan<char> line,
		out int lineStart)
	{
		if (nextLineStart > content.Length)
		{
			line = default;
			lineStart = 0;
			return false;
		}

		lineStart = nextLineStart;
		var relativeEnd = content[lineStart..].IndexOf('\n');
		var lineEnd = relativeEnd < 0 ? content.Length : lineStart + relativeEnd;
		var contentEnd = lineEnd > lineStart && content[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
		line = content[lineStart..contentEnd];
		nextLineStart = relativeEnd < 0 ? content.Length + 1 : lineEnd + 1;
		return true;
	}

	private static ReadOnlySpan<char> TrimKey(ReadOnlySpan<char> key)
	{
		while (!key.IsEmpty && key[0] is ' ' or '\t' or '\'' or '"')
			key = key[1..];
		while (!key.IsEmpty && key[^1] is ' ' or '\t' or '\'' or '"')
			key = key[..^1];
		return key;
	}

	private static int IndexOfFirstNonWhitespace(ReadOnlySpan<char> value)
	{
		for (var index = 0; index < value.Length; index++)
			if (!char.IsWhiteSpace(value[index]))
				return index;
		return -1;
	}

	private static bool IsConnectionKeyCharacter(char character) =>
		char.IsLetterOrDigit(character) || character is ' ' or '_' or '-';

	private static bool IsConfigKeyCharacter(char character) =>
		char.IsLetterOrDigit(character) || character is '_' or '-' or '.';

	private static bool IsXmlNameCharacter(char character) =>
		char.IsLetterOrDigit(character) || character is '_' or '-' or '.' or ':';

	private static bool Contains(ReadOnlySpan<char> value, string candidate) =>
		value.Contains(candidate, StringComparison.OrdinalIgnoreCase);

	private readonly record struct TextSpan(int Start, int Length)
	{
		public int End => Start + Length;
	}

	private readonly record struct XmlAttributeSpan(
		string Name,
		int ValueStart,
		int ValueLength);

	private enum StructuredSecretFileKind
	{
		None,
		Environment,
		Configuration,
		Xml,
		Python
	}
}
