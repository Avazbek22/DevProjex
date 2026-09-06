using System.Text.RegularExpressions;

namespace DependencyFactsSpike;

internal abstract partial class LanguageFactAdapterBase : ILanguageFactAdapter
{
	protected static SourceSite Site(ExtractionContext context, SyntaxCapture capture) =>
		new(context.RelativePath, capture.Line, OneLine(capture.Text));

	protected static string OneLine(string value)
	{
		var line = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
		return line.Length <= 240 ? line : line[..237] + "...";
	}

	protected static string Identifier(string value)
	{
		var match = IdentifierRegex().Match(value);
		return match.Success ? match.Value : value.Trim();
	}

	protected static int GenericArity(string declaration)
	{
		var open = declaration.IndexOf('<');
		if (open < 0)
			return 0;
		var close = declaration.IndexOf('>', open + 1);
		if (close < 0)
			return 0;
		return declaration[(open + 1)..close].Split(',', StringSplitOptions.RemoveEmptyEntries).Length;
	}

	protected static IReadOnlyList<ReferenceFact> DistinctReferences(IEnumerable<ReferenceFact> references) =>
		references
			.GroupBy(static item => (item.Layer, item.Name, item.GenericArity, item.Line, item.SyntaxKind))
			.Select(static group => group.First())
			.OrderBy(static item => item.Line)
			.ThenBy(static item => item.Name, StringComparer.Ordinal)
			.ToArray();

	[GeneratedRegex(@"[A-Za-z_$][A-Za-z0-9_$]*", RegexOptions.CultureInvariant)]
	private static partial Regex IdentifierRegex();

	public abstract FileFacts Extract(ExtractionContext context);
}

internal sealed partial class CSharpFactAdapter : LanguageFactAdapterBase
{
	private static readonly IReadOnlyDictionary<string, SymbolKind> Kinds =
		new Dictionary<string, SymbolKind>(StringComparer.Ordinal)
		{
			["declaration.class"] = SymbolKind.Class,
			["declaration.struct"] = SymbolKind.Struct,
			["declaration.interface"] = SymbolKind.Interface,
			["declaration.record"] = SymbolKind.Record,
			["declaration.enum"] = SymbolKind.Enum,
			["declaration.delegate"] = SymbolKind.Delegate
		};

	public override FileFacts Extract(ExtractionContext context)
	{
		var namespaces = ParseNamespaces(context.Declarations);
		var aliases = ParseUsings(context.Declarations, out var usingNamespaces);
		var typeParameters = context.References
			.Where(static capture => capture.Name == "context.type_parameters")
			.SelectMany(static capture => TypeParameterRegex().Matches(capture.Text).Select(match => match.Groups["name"].Value))
			.Distinct(StringComparer.Ordinal)
			.OrderBy(static value => value, StringComparer.Ordinal)
			.ToArray();
		var declarationCaptures = context.Declarations.Where(capture => Kinds.ContainsKey(capture.Name)).ToArray();
		var declarations = new List<DeclarationFact>(declarationCaptures.Length);
		foreach (var capture in declarationCaptures)
		{
			var match = CSharpDeclarationRegex().Match(capture.Text);
			if (!match.Success)
				continue;
			var name = match.Groups["name"].Value;
			var containingNamespace = namespaces
				.Where(item => item.StartIndex <= capture.StartIndex && item.EndIndex >= capture.EndIndex)
				.OrderBy(item => item.EndIndex - item.StartIndex)
				.Select(static item => item.Name)
				.FirstOrDefault() ?? namespaces.FirstOrDefault(static item => item.IsFileScoped)?.Name ?? string.Empty;
			var parents = declarationCaptures
				.Where(item => item.StartIndex < capture.StartIndex && item.EndIndex >= capture.EndIndex)
				.OrderBy(static item => item.StartIndex)
				.Select(item => CSharpDeclarationRegex().Match(item.Text))
				.Where(static item => item.Success)
				.Select(static item => item.Groups["name"].Value + AritySuffix(GenericArity(item.Value)))
				.ToArray();
			var qualified = string.Join('.', new[] { containingNamespace }.Concat(parents).Append(name + AritySuffix(GenericArity(capture.Text)))
				.Where(static value => value.Length > 0));
			var scope = FileModifierRegex().IsMatch(capture.Text)
				? context.ScopeId + "#file:" + context.RelativePath
				: context.ScopeId;
			declarations.Add(new DeclarationFact(
				new SymbolIdentity(scope, context.Language, Kinds[capture.Name], qualified, GenericArity(capture.Text)),
				[Site(context, capture)]));
		}

		var references = context.References
			.Where(static capture => capture.Name.StartsWith("reference.", StringComparison.Ordinal))
			.SelectMany(ExtractTypeReferences)
			.Where(reference => !declarations.Any(declaration =>
				declaration.Sites[0].Line == reference.Line &&
				declaration.Identity.QualifiedName.EndsWith('.' + reference.Name, StringComparison.Ordinal)))
			.ToArray();
		return new FileFacts(
			context.RelativePath,
			context.ScopeId,
			context.Language,
			context.ContentHash,
			context.HasSyntaxErrors,
			declarations,
			[],
			DistinctReferences(references),
			usingNamespaces.OrderBy(static item => item, StringComparer.Ordinal).ToArray(),
			aliases,
			typeParameters);
	}

	private static IReadOnlyList<NamespaceSpan> ParseNamespaces(IReadOnlyList<SyntaxCapture> captures) =>
		captures.Where(static capture => capture.Name == "context.namespace")
			.Select(capture =>
			{
				var match = NamespaceRegex().Match(capture.Text);
				return new NamespaceSpan(
					match.Success ? match.Groups["name"].Value : string.Empty,
					capture.StartIndex,
					capture.EndIndex,
					capture.NodeType == "file_scoped_namespace_declaration");
			})
			.Where(static item => item.Name.Length > 0)
			.ToArray();

	private static IReadOnlyDictionary<string, string> ParseUsings(
		IReadOnlyList<SyntaxCapture> captures,
		out HashSet<string> namespaces)
	{
		namespaces = new HashSet<string>(StringComparer.Ordinal);
		var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var capture in captures.Where(static capture => capture.Name == "context.using"))
		{
			var match = UsingRegex().Match(capture.Text);
			if (!match.Success || match.Groups["static"].Success)
				continue;
			var target = match.Groups["target"].Value;
			if (match.Groups["alias"].Success)
				aliases[match.Groups["alias"].Value] = target;
			else
				namespaces.Add(target);
		}
		return aliases;
	}

	private static IEnumerable<ReferenceFact> ExtractTypeReferences(SyntaxCapture capture)
	{
		var text = capture.Text;
		var candidates = capture.Name switch
		{
			"reference.variable_type" => PrefixBeforeVariableRegex().Match(text).Groups["type"].Value,
			"reference.property_type" => PropertyTypeRegex().Match(text).Groups["type"].Value,
			"reference.parameter_type" => ParameterTypeRegex().Match(text).Groups["type"].Value,
			"reference.return_type" => ReturnTypeRegex().Match(text).Groups["type"].Value,
			"reference.base" => text.TrimStart(':', ' '),
			"reference.generic_argument" => text.Trim('<', '>', ' '),
			"reference.attribute" => AttributeRegex().Match(text).Groups["type"].Value,
			"reference.object_creation" => ObjectCreationRegex().Match(text).Groups["type"].Value,
			_ => ParenthesizedTypeRegex().Match(text).Groups["type"].Value
		};
		if (string.IsNullOrWhiteSpace(candidates))
			yield break;
		foreach (Match match in TypeNameRegex().Matches(candidates))
		{
			var value = match.Value.Replace("global::", string.Empty, StringComparison.Ordinal);
			var name = value.Split(new[] { '.', ':' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? value;
			if (CSharpKeywords.Contains(name))
				continue;
			yield return new ReferenceFact(
				EvidenceLayer.TypeReference,
				name,
				0,
				capture.Line,
				OneLine(capture.Text),
				capture.NodeType);
		}
	}

	private static string AritySuffix(int arity) => arity == 0 ? string.Empty : $"`{arity}";

	private sealed record NamespaceSpan(string Name, int StartIndex, int EndIndex, bool IsFileScoped);

	private static readonly HashSet<string> CSharpKeywords = new(
		["public", "private", "protected", "internal", "static", "readonly", "ref", "out", "in", "params", "this", "where", "new", "class", "struct", "interface", "record", "enum", "delegate", "void", "var", "get", "set", "init", "return"],
		StringComparer.Ordinal);

	[GeneratedRegex(@"\b(?:class|struct|interface|record(?:\s+class|\s+struct)?|enum|delegate)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
	private static partial Regex CSharpDeclarationRegex();
	[GeneratedRegex(@"\bnamespace\s+(?<name>[A-Za-z_][A-Za-z0-9_.]*)", RegexOptions.CultureInvariant)]
	private static partial Regex NamespaceRegex();
	[GeneratedRegex(@"\busing\s+(?<static>static\s+)?(?:(?<alias>[A-Za-z_]\w*)\s*=\s*)?(?<target>(?:global::)?[A-Za-z_]\w*(?:(?:\.|::)[A-Za-z_]\w*)*)\s*;", RegexOptions.CultureInvariant)]
	private static partial Regex UsingRegex();
	[GeneratedRegex(@"\bfile\s+(?:(?:sealed|abstract|partial|readonly|unsafe)\s+)*(?:class|struct|interface|record|enum|delegate)\b", RegexOptions.CultureInvariant)]
	private static partial Regex FileModifierRegex();
	[GeneratedRegex(@"(?<name>[A-Za-z_]\w*)", RegexOptions.CultureInvariant)]
	private static partial Regex TypeParameterRegex();
	[GeneratedRegex(@"^(?:(?:public|private|protected|internal|static|readonly|volatile|const|ref|scoped|unsafe|new)\s+)*(?<type>[A-Za-z_][\w.:<>,?\[\]\s]*)\s+[A-Za-z_]\w*", RegexOptions.CultureInvariant)]
	private static partial Regex PrefixBeforeVariableRegex();
	[GeneratedRegex(@"^(?:(?:public|private|protected|internal|static|virtual|abstract|override|sealed|required|new|readonly|unsafe)\s+)*(?<type>[A-Za-z_][\w.:<>,?\[\]\s]*)\s+[A-Za-z_]\w*\s*\{", RegexOptions.CultureInvariant)]
	private static partial Regex PropertyTypeRegex();
	[GeneratedRegex(@"^(?:\[[^\]]+\]\s*)*(?:(?:this|ref|out|in|params|scoped)\s+)*(?<type>[A-Za-z_][\w.:<>,?\[\]\s]*)\s+[A-Za-z_]\w*", RegexOptions.CultureInvariant)]
	private static partial Regex ParameterTypeRegex();
	[GeneratedRegex(@"^(?:(?:public|private|protected|internal|static|virtual|abstract|override|sealed|async|extern|unsafe|new|partial)\s+)*(?<type>[A-Za-z_][\w.:<>,?\[\]\s]*)\s+[A-Za-z_]\w*\s*(?:<[^>]+>)?\s*\(", RegexOptions.CultureInvariant)]
	private static partial Regex ReturnTypeRegex();
	[GeneratedRegex(@"^\[\s*(?<type>[A-Za-z_][\w.:]*)", RegexOptions.CultureInvariant)]
	private static partial Regex AttributeRegex();
	[GeneratedRegex(@"\bnew\s+(?<type>[A-Za-z_][\w.:]*(?:\s*<[^>;(){}]+>)?)", RegexOptions.CultureInvariant)]
	private static partial Regex ObjectCreationRegex();
	[GeneratedRegex(@"\((?<type>[A-Za-z_][\w.:<>,?\[\]\s]*)\)", RegexOptions.CultureInvariant)]
	private static partial Regex ParenthesizedTypeRegex();
	[GeneratedRegex(@"(?:global::)?[A-Za-z_]\w*(?:(?:\.|::)[A-Za-z_]\w*)*", RegexOptions.CultureInvariant)]
	private static partial Regex TypeNameRegex();
}

internal sealed partial class TypeScriptFactAdapter : LanguageFactAdapterBase
{
	public override FileFacts Extract(ExtractionContext context)
	{
		var moduleName = Path.ChangeExtension(context.RelativePath, null)!.Replace('\\', '/');
		var declarations = context.Declarations.Select(capture => ToDeclaration(context, capture, moduleName))
			.Where(static declaration => declaration is not null)
			.Cast<DeclarationFact>()
			.ToArray();
		var imports = context.References
			.Where(static capture => capture.Name.StartsWith("import.", StringComparison.Ordinal))
			.SelectMany(ExtractImports)
			.ToArray();
		var references = context.References
			.Where(static capture => capture.Name.StartsWith("reference.", StringComparison.Ordinal))
			.SelectMany(ExtractTypes)
			.ToArray();
		return new FileFacts(
			context.RelativePath,
			context.ScopeId,
			context.Language,
			context.ContentHash,
			context.HasSyntaxErrors,
			declarations,
			imports,
			DistinctReferences(references),
			[],
			new Dictionary<string, string>(),
			[]);
	}

	private static DeclarationFact? ToDeclaration(ExtractionContext context, SyntaxCapture capture, string module)
	{
		var match = TsDeclarationRegex().Match(capture.Text);
		if (!match.Success)
			return null;
		var kind = capture.Name switch
		{
			"declaration.class" => SymbolKind.Class,
			"declaration.interface" => SymbolKind.Interface,
			"declaration.enum" => SymbolKind.Enum,
			"declaration.function" => SymbolKind.Function,
			"declaration.module" => SymbolKind.Module,
			_ => SymbolKind.Record
		};
		var name = match.Groups["name"].Value;
		return new DeclarationFact(
			new SymbolIdentity(context.ScopeId, context.Language, kind, $"{module}#{name}", GenericArity(capture.Text)),
			[Site(context, capture)]);
	}

	private static IEnumerable<ImportContext> ExtractImports(SyntaxCapture capture)
	{
		if (capture.Name == "import.call")
		{
			var require = RequireRegex().Match(capture.Text);
			if (!require.Success)
				yield break;
			yield return new ImportContext(require.Groups["path"].Value, null, null, false, 0, capture.Line, OneLine(capture.Text));
			yield break;
		}

		var match = ModuleSpecifierRegex().Match(capture.Text);
		if (!match.Success)
			yield break;
		var specifier = match.Groups["path"].Value;
		var names = NamedImportsRegex().Match(capture.Text);
		if (names.Success)
		{
			foreach (var raw in names.Groups["names"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var parts = Regex.Split(raw, @"\s+as\s+", RegexOptions.CultureInvariant);
				yield return new ImportContext(specifier, parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : null, false, 0, capture.Line, OneLine(capture.Text));
			}
		}
		else
		{
			yield return new ImportContext(specifier, null, null, capture.Text.Contains('*'), 0, capture.Line, OneLine(capture.Text));
		}
	}

	private static IEnumerable<ReferenceFact> ExtractTypes(SyntaxCapture capture)
	{
		foreach (Match match in TsTypeRegex().Matches(capture.Text))
		{
			var value = match.Value;
			if (TsKeywords.Contains(value))
				continue;
			yield return new ReferenceFact(EvidenceLayer.TypeReference, value.Split('.').Last(), 0, capture.Line, OneLine(capture.Text), capture.NodeType);
		}
	}

	private static readonly HashSet<string> TsKeywords = new(
		["string", "number", "boolean", "unknown", "never", "any", "void", "null", "undefined", "keyof", "typeof", "readonly", "new", "extends", "implements"],
		StringComparer.Ordinal);

	[GeneratedRegex(@"\b(?:class|interface|type|enum|function|namespace|module)\s+(?<name>[A-Za-z_$][\w$]*)", RegexOptions.CultureInvariant)]
	private static partial Regex TsDeclarationRegex();
	[GeneratedRegex("(?:from\\s+|import\\s*\\(|require\\s*\\()\\s*['\"](?<path>[^'\"]+)['\"]", RegexOptions.CultureInvariant)]
	private static partial Regex ModuleSpecifierRegex();
	[GeneratedRegex("""require\s*\(\s*['"](?<path>[^'"]+)['"]\s*\)""", RegexOptions.CultureInvariant)]
	private static partial Regex RequireRegex();
	[GeneratedRegex(@"\{(?<names>[^}]+)\}", RegexOptions.CultureInvariant)]
	private static partial Regex NamedImportsRegex();
	[GeneratedRegex(@"[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*", RegexOptions.CultureInvariant)]
	private static partial Regex TsTypeRegex();
}

internal sealed partial class PythonFactAdapter : LanguageFactAdapterBase
{
	public override FileFacts Extract(ExtractionContext context)
	{
		var module = PythonModuleName(context.RelativePath);
		var declarations = context.Declarations.Select(capture =>
		{
			var match = PythonDeclarationRegex().Match(capture.Text);
			if (!match.Success)
				return null;
			var kind = capture.Name == "declaration.class" ? SymbolKind.Class : SymbolKind.Function;
			return new DeclarationFact(
				new SymbolIdentity(context.ScopeId, context.Language, kind, $"{module}.{match.Groups["name"].Value}", 0),
				[Site(context, capture)]);
		}).Where(static item => item is not null).Cast<DeclarationFact>().ToArray();
		var imports = context.References
			.Where(static capture => capture.Name.StartsWith("import.", StringComparison.Ordinal))
			.SelectMany(ExtractImports)
			.ToArray();
		var references = context.References
			.Where(static capture => capture.Name.StartsWith("reference.", StringComparison.Ordinal))
			.SelectMany(capture => PythonTypeRegex().Matches(capture.Text)
				.Select(match => match.Value)
				.Where(value => !PythonKeywords.Contains(value))
				.Select(value => new ReferenceFact(EvidenceLayer.TypeReference, value.Split('.').Last(), 0, capture.Line, OneLine(capture.Text), capture.NodeType)))
			.ToArray();
		return new FileFacts(context.RelativePath, context.ScopeId, context.Language, context.ContentHash,
			context.HasSyntaxErrors, declarations, imports, DistinctReferences(references), [], new Dictionary<string, string>(), []);
	}

	private static IEnumerable<ImportContext> ExtractImports(SyntaxCapture capture)
	{
		if (capture.Name == "import.direct")
		{
			var body = capture.Text["import".Length..];
			foreach (var item in body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var parts = Regex.Split(item, @"\s+as\s+", RegexOptions.CultureInvariant);
				yield return new ImportContext(parts[0], null, parts.Length > 1 ? parts[1] : null, false, 0, capture.Line, OneLine(capture.Text));
			}
			yield break;
		}

		var match = PythonFromRegex().Match(capture.Text);
		if (!match.Success)
			yield break;
		var dotted = match.Groups["module"].Value;
		var relativeLevel = dotted.TakeWhile(static character => character == '.').Count();
		var module = dotted[relativeLevel..];
		foreach (var item in match.Groups["names"].Value.Trim('(', ')', ' ').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var parts = Regex.Split(item, @"\s+as\s+", RegexOptions.CultureInvariant);
			yield return new ImportContext(module, parts[0], parts.Length > 1 ? parts[1] : null, parts[0] == "*", relativeLevel, capture.Line, OneLine(capture.Text));
		}
	}

	private static string PythonModuleName(string relativePath)
	{
		var path = Path.ChangeExtension(relativePath, null)!.Replace('/', '.').Replace('\\', '.');
		return path.EndsWith(".__init__", StringComparison.Ordinal) ? path[..^".__init__".Length] : path;
	}

	private static readonly HashSet<string> PythonKeywords = new(
		["def", "class", "None", "True", "False", "str", "int", "float", "bool", "bytes", "list", "dict", "tuple", "set", "object", "typing", "self", "cls"],
		StringComparer.Ordinal);

	[GeneratedRegex(@"\b(?:class|def)\s+(?<name>[A-Za-z_]\w*)", RegexOptions.CultureInvariant)]
	private static partial Regex PythonDeclarationRegex();
	[GeneratedRegex(@"^from\s+(?<module>\.*(?:[A-Za-z_][\w.]*)?)\s+import\s+(?<names>.+)$", RegexOptions.CultureInvariant)]
	private static partial Regex PythonFromRegex();
	[GeneratedRegex(@"[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*", RegexOptions.CultureInvariant)]
	private static partial Regex PythonTypeRegex();
}
