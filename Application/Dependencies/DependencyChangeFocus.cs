using System.Text;
using System.Text.RegularExpressions;
using DevProjex.Application.Diff;

namespace DevProjex.Application.Dependencies;

/// <summary>A new-side line a change touched; a removal is placed on the line that now follows it.</summary>
public sealed record DependencyChangedLine(int Line, string? AddedText, string? RemovedText);

/// <param name="Name">The top-level name other files import or reference.</param>
/// <param name="Members">Nested declarations the change touched inside <paramref name="Name"/>, by qualified name.</param>
/// <param name="FirstLine">The first changed line inside the declaration.</param>
/// <param name="DeclarationLine">
/// The line that declares <paramref name="Name"/> in the current version, or 0 for an assignment
/// the change removed; a caller confirms the name there before showing it.
/// </param>
public sealed record DependencyChangedDeclaration(
	string Name,
	IReadOnlyList<string> Members,
	int FirstLine,
	int DeclarationLine);

public enum DependencyBindingKind
{
	/// <summary>The imported name is bound locally, under its own name or an alias.</summary>
	Name,

	/// <summary>The target module is bound to a qualifier, so names are used as <c>qualifier.Name</c>.</summary>
	Qualifier,

	/// <summary>Every public name of the target is bound under its own name.</summary>
	Wildcard,

	/// <summary>
	/// The index recorded the import without its names, as it does for TypeScript and JavaScript;
	/// the names are read from the statement text when uses are found.
	/// </summary>
	Statement
}

/// <param name="Local">The local name or qualifier; empty for a wildcard.</param>
/// <param name="Imported">The name taken from the target, for <see cref="DependencyBindingKind.Name"/>.</param>
public sealed record DependencyNameBinding(DependencyBindingKind Kind, string Local, string? Imported, int ImportLine);

/// <param name="Uses">Lines using each changed name, outside the import statements.</param>
/// <param name="Bound">Changed names the file imports, whether or not it then uses them.</param>
public sealed record DependencyNameUses(
	IReadOnlyDictionary<string, IReadOnlyList<int>> Uses,
	IReadOnlySet<string> Bound)
{
	public bool UsesAny => Uses.Count > 0;

	/// <summary>The file binds changed names and never uses them, as a package entry point re-exporting them does.</summary>
	public bool OnlyReExports => Uses.Count == 0 && Bound.Count > 0;
}

/// <summary>
/// Pure helpers that relate a change to the declarations it touched and to the files that use
/// those declarations. Callers supply protected text; nothing here reads files or Git.
/// </summary>
public static partial class DependencyChangeFocus
{
	public static IReadOnlyList<DependencyChangedLine> ChangedLines(IReadOnlyList<UnifiedDiffHunk> hunks)
	{
		ArgumentNullException.ThrowIfNull(hunks);
		var lines = new SortedDictionary<int, DependencyChangedLine>();
		foreach (var hunk in hunks)
		{
			// A side with no lines starts at the line before the hunk.
			var newLine = hunk.NewLines == 0 ? hunk.NewStart + 1 : hunk.NewStart;
			foreach (var line in hunk.Lines)
			{
				switch (line.Kind)
				{
					case UnifiedDiffLineKind.Context:
						newLine++;
						break;
					case UnifiedDiffLineKind.Added:
						Merge(lines, new DependencyChangedLine(newLine, line.Text, null));
						newLine++;
						break;
					case UnifiedDiffLineKind.Removed:
						Merge(lines, new DependencyChangedLine(Math.Max(1, newLine), null, line.Text));
						break;
				}
			}
		}
		return lines.Values.ToArray();
	}

	private static void Merge(SortedDictionary<int, DependencyChangedLine> lines, DependencyChangedLine line)
	{
		if (!lines.TryGetValue(line.Line, out var existing))
		{
			lines.Add(line.Line, line);
			return;
		}
		lines[line.Line] = existing with
		{
			AddedText = existing.AddedText ?? line.AddedText,
			RemovedText = existing.RemovedText ?? line.RemovedText
		};
	}

	/// <summary>
	/// The top-level declarations the changed lines fall in, in order of their first changed
	/// line, and the changed lines that fall in none. A module-level assignment counts as a
	/// declaration for Python and TypeScript/JavaScript, which the declaration index does not hold.
	/// </summary>
	public static (IReadOnlyList<DependencyChangedDeclaration> Declarations, IReadOnlyList<int> Unmatched) FindChangedDeclarations(
		FileFacts seed,
		IReadOnlyList<DependencyChangedLine> changedLines)
	{
		ArgumentNullException.ThrowIfNull(seed);
		ArgumentNullException.ThrowIfNull(changedLines);
		var byName = new Dictionary<string, (int FirstLine, int DeclarationLine, List<string> Members)>(StringComparer.Ordinal);
		var order = new List<string>();
		var unmatched = new List<int>();
		var declarations = seed.NavigationDeclarations
			.Where(static declaration => declaration.Kind != NavigationSymbolKind.Module)
			.ToArray();
		foreach (var changed in changedLines)
		{
			var containing = declarations
				.Where(declaration => declaration.StartLine <= changed.Line && changed.Line <= declaration.EndLine)
				.ToArray();
			if (containing.Length > 0)
			{
				var outermost = containing
					.OrderByDescending(static declaration => declaration.EndLine - declaration.StartLine)
					.ThenBy(static declaration => declaration.StartLine)
					.First();
				var innermost = containing
					.OrderBy(static declaration => declaration.EndLine - declaration.StartLine)
					.ThenByDescending(static declaration => declaration.StartLine)
					.First();
				var name = TopLevelName(outermost);
				if (name.Length == 0)
				{
					unmatched.Add(changed.Line);
					continue;
				}
				var member = ReferenceEquals(innermost, outermost) ? null : innermost.Name;
				Add(name, changed.Line, outermost.StartLine, member);
				continue;
			}
			if (AssignedName(seed, changed.AddedText) is { } assigned)
			{
				Add(assigned, changed.Line, changed.Line, member: null);
				continue;
			}
			if (AssignedName(seed, changed.RemovedText, removed: true) is { } removedName)
			{
				Add(removedName, changed.Line, 0, member: null);
				continue;
			}
			unmatched.Add(changed.Line);
		}
		return (order
			.Select(name => new DependencyChangedDeclaration(
				name,
				byName[name].Members,
				byName[name].FirstLine,
				byName[name].DeclarationLine))
			.ToArray(), unmatched);

		void Add(string name, int line, int declarationLine, string? member)
		{
			if (!byName.TryGetValue(name, out var entry))
			{
				entry = (line, declarationLine, []);
				byName.Add(name, entry);
				order.Add(name);
			}
			if (member is not null && !entry.Members.Contains(member, StringComparer.Ordinal))
				entry.Members.Add(member);
		}
	}

	private static string TopLevelName(NavigationDeclaration declaration)
	{
		var name = declaration.Name;
		if (declaration.Owner is { Length: > 0 } owner && name.StartsWith(owner, StringComparison.Ordinal))
			name = name[owner.Length..].TrimStart('.', ':', '#');
		// Java numbers repeated declarations as "Name#2"; the referenced name has no suffix.
		var ordinal = name.IndexOf('#');
		if (ordinal > 0)
			name = name[..ordinal];
		return IsIdentifier(name) ? name : string.Empty;
	}

	private static string? AssignedName(FileFacts seed, string? text, bool removed = false)
	{
		if (string.IsNullOrEmpty(text))
			return null;
		Match match;
		switch (seed.LanguageId)
		{
			case LanguageId.Python:
				match = PythonAssignmentRegex().Match(text);
				if (!match.Success)
					return null;
				var name = match.Groups["name"].Value;
				// A removed assignment no longer exists in the indexed version; any other must be one
				// the extractor recorded at module level.
				return removed || seed.Aliases.ContainsKey("$python-assignment:" + name) ? name : null;
			case LanguageId.TypeScript or LanguageId.Tsx or LanguageId.JavaScript:
				match = ScriptAssignmentRegex().Match(text);
				return match.Success ? match.Groups["name"].Value : null;
			default:
				return null;
		}
	}

	/// <summary>
	/// How <paramref name="dependent"/> binds names of <paramref name="targetPath"/> through the
	/// imports on <paramref name="importLines"/>, the lines of its resolved import edges to it.
	/// </summary>
	public static IReadOnlyList<DependencyNameBinding> Bindings(
		FileFacts dependent,
		string targetPath,
		IReadOnlySet<int> importLines)
	{
		ArgumentNullException.ThrowIfNull(dependent);
		ArgumentNullException.ThrowIfNull(targetPath);
		ArgumentNullException.ThrowIfNull(importLines);
		var targetModule = ModuleStem(targetPath);
		var bindings = new List<DependencyNameBinding>();
		foreach (var import in dependent.Imports)
		{
			if (!importLines.Contains(import.Site.Line))
				continue;
			var binding = dependent.LanguageId switch
			{
				LanguageId.Python => PythonBinding(import, targetModule),
				LanguageId.TypeScript or LanguageId.Tsx or LanguageId.JavaScript => ScriptBinding(import),
				_ => import.ImportedName is { Length: > 0 } name && IsIdentifier(name)
					? new DependencyNameBinding(DependencyBindingKind.Name, import.Alias ?? name, name, import.Site.Line)
					: null
			};
			if (binding is not null)
				bindings.Add(binding);
		}
		return bindings;
	}

	private static DependencyNameBinding? PythonBinding(ImportFact import, string targetModule)
	{
		var line = import.Site.Line;
		if (import.IsWildcard)
			return new DependencyNameBinding(DependencyBindingKind.Wildcard, string.Empty, null, line);
		if (import.ImportedName is null)
		{
			// "import a.b" binds "a" and is used as "a.b.Name"; "import a.b as c" binds "c".
			var qualifier = import.Alias ?? import.Specifier;
			return IsQualifier(qualifier) ? new DependencyNameBinding(DependencyBindingKind.Qualifier, qualifier, null, line) : null;
		}
		var imported = import.ImportedName;
		if (!IsIdentifier(imported))
			return null;
		var local = import.Alias ?? imported;
		// "from . import _config" binds the module itself.
		return string.Equals(imported, targetModule, StringComparison.Ordinal)
			? new DependencyNameBinding(DependencyBindingKind.Qualifier, local, null, line)
			: new DependencyNameBinding(DependencyBindingKind.Name, local, imported, line);
	}

	private static DependencyNameBinding? ScriptBinding(ImportFact import)
	{
		var line = import.Site.Line;
		if (import.IsWildcard)
			return import.Alias is { Length: > 0 } alias && IsIdentifier(alias)
				? new DependencyNameBinding(DependencyBindingKind.Qualifier, alias, null, line)
				: new DependencyNameBinding(DependencyBindingKind.Wildcard, string.Empty, null, line);
		if (import.ImportedName is not { Length: > 0 } imported)
			return import.Alias is { Length: > 0 } module && IsIdentifier(module)
				? new DependencyNameBinding(DependencyBindingKind.Qualifier, module, null, line)
				: new DependencyNameBinding(DependencyBindingKind.Statement, string.Empty, null, line);
		if (imported == "default" || !IsIdentifier(imported))
			return null;
		return new DependencyNameBinding(DependencyBindingKind.Name, import.Alias ?? imported, imported, line);
	}

	/// <summary>
	/// The lines of <paramref name="content"/> that use each of <paramref name="names"/> through
	/// <paramref name="bindings"/>. A wildcard binds only <paramref name="wildcardNames"/>.
	/// Occurrences inside an import statement, a comment line, or a range
	/// <paramref name="isProtected"/> reports are not uses.
	/// </summary>
	public static DependencyNameUses FindUses(
		string content,
		LanguageId language,
		IReadOnlyList<DependencyNameBinding> bindings,
		IReadOnlyCollection<string> names,
		IReadOnlySet<string> wildcardNames,
		Func<int, int, bool> isProtected,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(content);
		ArgumentNullException.ThrowIfNull(bindings);
		ArgumentNullException.ThrowIfNull(names);
		ArgumentNullException.ThrowIfNull(wildcardNames);
		ArgumentNullException.ThrowIfNull(isProtected);
		var importLines = ImportStatementLines(content, bindings.Select(static binding => binding.ImportLine));
		bindings = ExpandStatementBindings(content, bindings);
		var bound = new HashSet<string>(StringComparer.Ordinal);
		var bareNames = new Dictionary<string, string>(StringComparer.Ordinal);
		var qualifiers = new List<string>();
		foreach (var binding in bindings)
		{
			switch (binding.Kind)
			{
				case DependencyBindingKind.Name when binding.Imported is { } imported && names.Contains(imported):
					bound.Add(imported);
					bareNames[binding.Local] = imported;
					break;
				case DependencyBindingKind.Wildcard:
					foreach (var name in names.Where(wildcardNames.Contains))
					{
						bound.Add(name);
						bareNames[name] = name;
					}
					break;
				case DependencyBindingKind.Qualifier:
					qualifiers.Add(binding.Local);
					break;
			}
		}
		var uses = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
		if (bareNames.Count == 0 && qualifiers.Count == 0)
			return new DependencyNameUses(new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal), bound);

		var lineNumber = 0;
		var start = 0;
		while (start <= content.Length)
		{
			cancellationToken.ThrowIfCancellationRequested();
			lineNumber++;
			var newline = content.IndexOf('\n', start);
			var end = newline < 0 ? content.Length : newline;
			if (!importLines.Contains(lineNumber) && !IsCommentLine(content, start, end, language))
			{
				foreach (var (local, imported) in bareNames)
				{
					if (ContainsToken(content, start, end, local, qualifier: null, isProtected))
						Record(imported, lineNumber);
				}
				foreach (var qualifier in qualifiers)
				{
					foreach (var name in names)
					{
						if (ContainsToken(content, start, end, name, qualifier, isProtected))
							Record(name, lineNumber);
					}
				}
			}
			if (newline < 0)
				break;
			start = newline + 1;
		}
		return new DependencyNameUses(
			uses.ToDictionary(
				static pair => pair.Key,
				static pair => (IReadOnlyList<int>)pair.Value.ToArray(),
				StringComparer.Ordinal),
			bound);

		void Record(string name, int line)
		{
			if (!uses.TryGetValue(name, out var lines))
				uses.Add(name, lines = []);
			lines.Add(line);
		}
	}

	/// <summary>
	/// The names a Python module exports to "from module import *": its literal <c>__all__</c>,
	/// or, without one, every name not starting with an underscore. Null when <c>__all__</c> is
	/// present but not a literal list of strings.
	/// </summary>
	public static IReadOnlySet<string>? PythonWildcardNames(string content, IEnumerable<string> names, bool dynamicAll)
	{
		ArgumentNullException.ThrowIfNull(content);
		ArgumentNullException.ThrowIfNull(names);
		if (dynamicAll)
			return null;
		var declared = PythonAllRegex().Match(content);
		if (!declared.Success)
		{
			return content.Contains("__all__", StringComparison.Ordinal) && PythonAllAssignmentRegex().IsMatch(content)
				? null
				: names.Where(static name => !name.StartsWith('_')).ToHashSet(StringComparer.Ordinal);
		}
		return PythonQuotedNameRegex().Matches(declared.Groups["items"].Value)
			.Select(static match => match.Groups["name"].Value)
			.ToHashSet(StringComparer.Ordinal);
	}

	/// <summary>The simple names of the changed members, as a member access writes them after a dot.</summary>
	public static IReadOnlyList<string> MemberNames(DependencyChangedDeclaration declaration)
	{
		ArgumentNullException.ThrowIfNull(declaration);
		return declaration.Members
			.Select(static member => member[(member.LastIndexOfAny(['.', ':', '#']) + 1)..])
			.Where(static member => IsIdentifier(member) && member != "constructor" && member != "__init__")
			.Distinct(StringComparer.Ordinal)
			.ToArray();
	}

	/// <summary>
	/// The lines of <paramref name="content"/> that access <paramref name="member"/> on some value,
	/// as <c>value.member</c>, outside comment lines and protected ranges.
	/// </summary>
	public static IReadOnlyList<int> MemberAccessLines(
		string content,
		LanguageId language,
		string member,
		Func<int, int, bool> isProtected)
	{
		ArgumentNullException.ThrowIfNull(content);
		ArgumentNullException.ThrowIfNull(member);
		ArgumentNullException.ThrowIfNull(isProtected);
		var lines = new List<int>();
		var lineNumber = 0;
		var start = 0;
		while (start <= content.Length)
		{
			lineNumber++;
			var newline = content.IndexOf('\n', start);
			var end = newline < 0 ? content.Length : newline;
			if (!IsCommentLine(content, start, end, language) && ContainsMemberAccess(content, start, end, member, isProtected))
				lines.Add(lineNumber);
			if (newline < 0)
				break;
			start = newline + 1;
		}
		return lines;
	}

	private static bool ContainsMemberAccess(string content, int lineStart, int lineEnd, string member, Func<int, int, bool> isProtected)
	{
		var searchFrom = lineStart;
		while (searchFrom < lineEnd)
		{
			var index = content.IndexOf(member, searchFrom, lineEnd - searchFrom, StringComparison.Ordinal);
			if (index < 0)
				return false;
			searchFrom = index + 1;
			var after = index + member.Length;
			if (after < lineEnd && IsIdentifierPart(content[after]))
				continue;
			var cursor = index - 1;
			while (cursor >= lineStart && content[cursor] is ' ' or '\t')
				cursor--;
			if (cursor < lineStart || content[cursor] != '.' || isProtected(cursor, after))
				continue;
			return true;
		}
		return false;
	}

	/// <summary>True when <paramref name="token"/> appears as a whole identifier on the given line.</summary>
	public static bool LineContainsToken(string content, int line, string token, Func<int, int, bool> isProtected)
	{
		ArgumentNullException.ThrowIfNull(content);
		ArgumentNullException.ThrowIfNull(token);
		ArgumentNullException.ThrowIfNull(isProtected);
		var start = 0;
		for (var current = 1; current < line; current++)
		{
			var newline = content.IndexOf('\n', start);
			if (newline < 0)
				return false;
			start = newline + 1;
		}
		var end = content.IndexOf('\n', start);
		return ContainsToken(content, start, end < 0 ? content.Length : end, token, qualifier: null, isProtected);
	}

	private static bool ContainsToken(
		string content,
		int lineStart,
		int lineEnd,
		string token,
		string? qualifier,
		Func<int, int, bool> isProtected)
	{
		var searchFrom = lineStart;
		while (searchFrom < lineEnd)
		{
			var index = content.IndexOf(token, searchFrom, lineEnd - searchFrom, StringComparison.Ordinal);
			if (index < 0)
				return false;
			searchFrom = index + 1;
			var after = index + token.Length;
			if (after < lineEnd && IsIdentifierPart(content[after]))
				continue;
			var tokenStart = index;
			if (qualifier is not null)
			{
				// Expect "qualifier . token", allowing spaces around the dot.
				var cursor = index - 1;
				while (cursor >= lineStart && content[cursor] is ' ' or '\t')
					cursor--;
				if (cursor < lineStart || content[cursor] != '.')
					continue;
				cursor--;
				while (cursor >= lineStart && content[cursor] is ' ' or '\t')
					cursor--;
				var qualifierStart = cursor - qualifier.Length + 1;
				if (qualifierStart < lineStart ||
					string.CompareOrdinal(content, qualifierStart, qualifier, 0, qualifier.Length) != 0)
					continue;
				tokenStart = qualifierStart;
			}
			if (tokenStart > lineStart && (IsIdentifierPart(content[tokenStart - 1]) || content[tokenStart - 1] == '.'))
				continue;
			// A name alone in quotes, as in __all__ or a string key, is text rather than a use.
			if (tokenStart > lineStart && after < lineEnd && content[tokenStart - 1] is '"' or '\'' or '`' &&
				content[after] == content[tokenStart - 1])
				continue;
			if (isProtected(tokenStart, after))
				continue;
			return true;
		}
		return false;
	}

	private static IReadOnlyList<DependencyNameBinding> ExpandStatementBindings(
		string content,
		IReadOnlyList<DependencyNameBinding> bindings)
	{
		if (bindings.All(static binding => binding.Kind != DependencyBindingKind.Statement))
			return bindings;
		var expanded = new List<DependencyNameBinding>(bindings.Count);
		foreach (var binding in bindings)
		{
			if (binding.Kind != DependencyBindingKind.Statement)
			{
				expanded.Add(binding);
				continue;
			}
			var statement = StatementText(content, binding.ImportLine);
			var line = binding.ImportLine;
			var match = ScriptImportRegex().Match(statement);
			if (match.Success)
			{
				if (match.Groups["namespace"].Success)
					expanded.Add(new DependencyNameBinding(DependencyBindingKind.Qualifier, match.Groups["namespace"].Value, null, line));
				if (match.Groups["named"].Success)
					expanded.AddRange(NamedBindings(match.Groups["named"].Value, line, " as "));
				continue;
			}
			match = ScriptReExportRegex().Match(statement);
			if (match.Success)
			{
				if (match.Groups["named"].Success)
					expanded.AddRange(NamedBindings(match.Groups["named"].Value, line, " as "));
				else if (!match.Groups["namespace"].Success)
					expanded.Add(new DependencyNameBinding(DependencyBindingKind.Wildcard, string.Empty, null, line));
				continue;
			}
			match = ScriptRequireRegex().Match(statement);
			if (!match.Success)
				continue;
			if (match.Groups["named"].Success)
				expanded.AddRange(NamedBindings(match.Groups["named"].Value, line, ":"));
			else
				expanded.Add(new DependencyNameBinding(DependencyBindingKind.Qualifier, match.Groups["module"].Value, null, line));
		}
		return expanded;
	}

	private static IEnumerable<DependencyNameBinding> NamedBindings(string list, int line, string aliasSeparator)
	{
		foreach (var item in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var text = item.StartsWith("type ", StringComparison.Ordinal) ? item[5..].Trim() : item;
			var separator = text.IndexOf(aliasSeparator, StringComparison.Ordinal);
			var imported = (separator < 0 ? text : text[..separator]).Trim();
			var local = (separator < 0 ? text : text[(separator + aliasSeparator.Length)..]).Trim();
			if (IsIdentifier(imported) && IsIdentifier(local) && imported != "default")
				yield return new DependencyNameBinding(DependencyBindingKind.Name, local, imported, line);
		}
	}

	/// <summary>The text of the import statement starting on <paramref name="line"/>, joined with spaces.</summary>
	private static string StatementText(string content, int line)
	{
		var lines = ImportStatementLines(content, [line]);
		var builder = new StringBuilder();
		var current = 1;
		var start = 0;
		while (start <= content.Length)
		{
			var newline = content.IndexOf('\n', start);
			var end = newline < 0 ? content.Length : newline;
			if (lines.Contains(current))
				builder.Append(content, start, end - start).Append(' ');
			else if (current > line)
				break;
			if (newline < 0)
				break;
			start = newline + 1;
			current++;
		}
		return builder.ToString();
	}

	/// <summary>
	/// The lines of the import statements starting on <paramref name="startLines"/>: a statement
	/// whose brackets stay open continues until they close, as a parenthesized Python import or a
	/// multi-line TypeScript import list does.
	/// </summary>
	private static HashSet<int> ImportStatementLines(string content, IEnumerable<int> startLines)
	{
		var starts = startLines.ToHashSet();
		var lines = new HashSet<int>(starts);
		if (starts.Count == 0)
			return lines;
		var lineNumber = 0;
		var start = 0;
		var depth = 0;
		var inStatement = false;
		while (start <= content.Length)
		{
			lineNumber++;
			var newline = content.IndexOf('\n', start);
			var end = newline < 0 ? content.Length : newline;
			if (starts.Contains(lineNumber))
			{
				inStatement = true;
				depth = 0;
			}
			if (inStatement)
			{
				lines.Add(lineNumber);
				for (var index = start; index < end; index++)
				{
					if (content[index] is '(' or '{' or '[') depth++;
					else if (content[index] is ')' or '}' or ']') depth--;
				}
				if (depth <= 0)
					inStatement = false;
			}
			if (newline < 0)
				break;
			start = newline + 1;
		}
		return lines;
	}

	private static bool IsCommentLine(string content, int start, int end, LanguageId language)
	{
		var index = start;
		while (index < end && char.IsWhiteSpace(content[index]))
			index++;
		if (index >= end)
			return true;
		var first = content[index];
		var second = index + 1 < end ? content[index + 1] : '\0';
		return language switch
		{
			LanguageId.Python or LanguageId.Ruby or LanguageId.Bash => first == '#',
			_ => first == '/' && second is '/' or '*' || first == '*'
		};
	}

	private static string ModuleStem(string path)
	{
		var fileName = path[(path.LastIndexOf('/') + 1)..];
		var stem = Path.GetFileNameWithoutExtension(fileName);
		if (stem is "__init__" or "index" && path.LastIndexOf('/') is var separator and > 0)
		{
			var directory = path[..separator];
			return directory[(directory.LastIndexOf('/') + 1)..];
		}
		return stem;
	}

	private static bool IsQualifier(string value) =>
		value.Length > 0 && value.Split('.').All(IsIdentifier);

	private static bool IsIdentifier(string value) =>
		value.Length > 0 && !char.IsDigit(value[0]) && value.All(IsIdentifierPart);

	private static bool IsIdentifierPart(char character) =>
		char.IsLetterOrDigit(character) || character is '_' or '$';

	[GeneratedRegex(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?::[^=]*)?=(?!=)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
	private static partial Regex PythonAssignmentRegex();

	[GeneratedRegex(@"^(?:export\s+)?(?:declare\s+)?(?:const|let|var)\s+(?<name>[A-Za-z_$][A-Za-z0-9_$]*)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
	private static partial Regex ScriptAssignmentRegex();

	[GeneratedRegex(@"\bimport\s+(?:type\s+)?(?:[A-Za-z_$][\w$]*\s*,?\s*)?(?:\{(?<named>[^}]*)\}|\*\s*as\s+(?<namespace>[A-Za-z_$][\w$]*))?\s*from\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
	private static partial Regex ScriptImportRegex();

	[GeneratedRegex(@"\bexport\s+(?:type\s+)?(?:\{(?<named>[^}]*)\}|\*(?:\s*as\s+(?<namespace>[A-Za-z_$][\w$]*))?)\s*from\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
	private static partial Regex ScriptReExportRegex();

	[GeneratedRegex(@"\b(?:const|let|var)\s+(?:\{(?<named>[^}]*)\}|(?<module>[A-Za-z_$][\w$]*))\s*=\s*require\s*\(", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
	private static partial Regex ScriptRequireRegex();

	[GeneratedRegex(@"^__all__\s*(?::[^=\n]*)?=\s*[\[(](?<items>[^\])]*)[\])]", RegexOptions.CultureInvariant | RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
	private static partial Regex PythonAllRegex();

	[GeneratedRegex(@"^__all__\s*(?::[^=\n]*)?[+]?=", RegexOptions.CultureInvariant | RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
	private static partial Regex PythonAllAssignmentRegex();

	[GeneratedRegex(@"[""'](?<name>[A-Za-z_][A-Za-z0-9_]*)[""']", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
	private static partial Regex PythonQuotedNameRegex();
}
