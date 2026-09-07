using System.Text.RegularExpressions;
using DevProjex.Application.Dependencies;

namespace DevProjex.Infrastructure.Dependencies;

internal sealed record DependencySyntaxCapture(
	string Name,
	string NodeType,
	string Text,
	int Line,
	int StartIndex,
	int EndIndex,
	string? CapturedName = null,
	int GenericArity = 0,
	bool IsFileLocal = false);

internal sealed record DependencyExtractionContext(
	string RelativePath,
	string ScopeId,
	LanguageId LanguageId,
	string Source,
	string ContentFingerprint,
	bool HasSyntaxErrors,
	IReadOnlyDictionary<string, int> ErrorNodeKinds,
	IReadOnlyList<DependencySyntaxCapture> Declarations,
	IReadOnlyList<DependencySyntaxCapture> References);

internal interface IDependencyLanguageAdapter
{
	FileFacts Extract(DependencyExtractionContext context, DependencyFactsLimits limits);
}

internal abstract partial class DependencyLanguageAdapter : IDependencyLanguageAdapter
{
	protected static SourceSite Site(DependencyExtractionContext context, DependencySyntaxCapture capture) =>
		new(context.RelativePath, capture.Line, OneLine(capture.Text));

	protected static string OneLine(string value)
	{
		var line = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
		return line.Length <= 240 ? line : line[..237] + "...";
	}

	protected static int GenericArityAt(string text, int position)
	{
		while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
		if (position >= text.Length || text[position] != '<') return 0;
		var depth = 0;
		var arity = 1;
		for (var index = position; index < text.Length; index++)
		{
			switch (text[index])
			{
				case '<': depth++; break;
				case '>' when --depth == 0: return arity;
				case ',' when depth == 1: arity++; break;
			}
		}
		return 0;
	}

	protected static IReadOnlyList<ReferenceFact> Distinct(IEnumerable<ReferenceFact> references) =>
		references.GroupBy(static fact =>
				(fact.Layer, fact.Name, fact.GenericArity, fact.Site.Line, fact.SyntaxKind))
			.Select(static group => group.First())
			.OrderBy(static fact => fact.Site.Line)
			.ThenBy(static fact => fact.Name, StringComparer.Ordinal)
			.ToArray();

	protected static FileFacts Complete(
		DependencyExtractionContext context,
		IReadOnlyList<DeclarationFact> declarations,
		IReadOnlyList<ImportFact> imports,
		IReadOnlyList<ReferenceFact> references,
		IReadOnlyList<string>? namespaces = null,
		IReadOnlyDictionary<string, string>? aliases = null,
		IReadOnlyList<string>? globalNamespaces = null,
		IReadOnlyDictionary<string, string>? globalAliases = null,
		IReadOnlyList<string>? typeParameters = null)
	{
		return new FileFacts(
			context.RelativePath,
			context.ScopeId,
			context.LanguageId,
			context.ContentFingerprint,
			context.Source.Length,
			DependencyFileStatus.Supported,
			null,
			context.HasSyntaxErrors,
			context.ErrorNodeKinds,
			declarations,
			imports,
			references,
			namespaces ?? [],
			aliases ?? new Dictionary<string, string>(),
			globalNamespaces ?? [],
			globalAliases ?? new Dictionary<string, string>(),
			typeParameters ?? []);
	}

	public abstract FileFacts Extract(DependencyExtractionContext context, DependencyFactsLimits limits);
}

internal sealed partial class CSharpDependencyLanguageAdapter : DependencyLanguageAdapter
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

	public override FileFacts Extract(DependencyExtractionContext context, DependencyFactsLimits limits)
	{
		var namespaces = ParseNamespaces(context.Declarations);
		var aliases = ParseUsings(
			context.Declarations,
			out var usingNamespaces,
			out var globalNamespaces,
			out var globalAliases);
		usingNamespaces.UnionWith(namespaces.Select(static item => item.Name));
		var typeParameters = context.References
			.Where(static capture => capture.Name == "context.type_parameters")
			.SelectMany(static capture => TypeParameterRegex().Matches(capture.Text)
				.Select(static match => match.Groups["name"].Value))
			.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
		var declarationCaptures = context.Declarations
			.Where(capture => Kinds.ContainsKey(capture.Name)).ToArray();
		var declarations = new List<DeclarationFact>(declarationCaptures.Length);
		foreach (var capture in declarationCaptures)
		{
			if (string.IsNullOrEmpty(capture.CapturedName))
				continue;
			var containingNamespace = namespaces
				.Where(item => item.Start <= capture.StartIndex && item.End >= capture.EndIndex)
				.OrderBy(item => item.End - item.Start).Select(static item => item.Name)
				.FirstOrDefault() ?? namespaces.FirstOrDefault(static item => item.FileScoped)?.Name ?? string.Empty;
			var parents = declarationCaptures
				.Where(item => item.StartIndex < capture.StartIndex && item.EndIndex >= capture.EndIndex)
				.OrderBy(static item => item.StartIndex)
				.Select(static item => string.IsNullOrEmpty(item.CapturedName)
					? null
					: item.CapturedName + AritySuffix(item.GenericArity))
				.OfType<string>();
			var name = capture.CapturedName;
			var qualified = string.Join('.', new[] { containingNamespace }
				.Concat(parents).Append(name + AritySuffix(capture.GenericArity))
				.Where(static value => value.Length > 0));
			declarations.Add(new DeclarationFact(
				new SymbolIdentity(
					context.ScopeId,
					context.LanguageId,
					Kinds[capture.Name],
					qualified,
					capture.GenericArity,
					capture.IsFileLocal ? context.RelativePath : null),
				[Site(context, capture)]));
		}

		var references = context.References
			.Where(static capture => capture.Name.StartsWith("reference.", StringComparison.Ordinal))
			.SelectMany(capture => ExtractReferences(context, capture))
			.Where(reference => !declarations.Any(declaration =>
				declaration.DeclarationSites[0].Line == reference.Site.Line &&
				SimpleName(declaration.Identity.QualifiedName) == reference.Name))
			.Take(limits.MaximumFactsPerFile + 1).ToArray();
		if (declarations.Count + references.Length > limits.MaximumFactsPerFile)
			return Failure(context, "fact limit exceeded");
		return Complete(
			context,
			declarations,
			[],
			Distinct(references),
			usingNamespaces.Order(StringComparer.Ordinal).ToArray(),
			aliases,
			globalNamespaces.Order(StringComparer.Ordinal).ToArray(),
			globalAliases,
			typeParameters);
	}

	private static IEnumerable<ReferenceFact> ExtractReferences(
		DependencyExtractionContext context,
		DependencySyntaxCapture capture)
	{
		if (capture.Name == "reference.target_typed_object_creation")
		{
			yield return NewReference(context, capture, "<target-typed-new>", 0);
			yield break;
		}
		var typeText = capture.Text;
		foreach (Match match in TypeNameRegex().Matches(typeText))
		{
			var name = match.Value.Replace("global::", string.Empty, StringComparison.Ordinal)
				.Replace("::", ".", StringComparison.Ordinal);
			var simpleName = name.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? name;
			if (!Keywords.Contains(simpleName))
				yield return NewReference(context, capture, name, GenericArityAt(typeText, match.Index + match.Length));
		}
	}

	private static ReferenceFact NewReference(DependencyExtractionContext context, DependencySyntaxCapture capture, string name, int arity) =>
		new(EvidenceLayer.TypeReference, name, arity,
			capture.Name.StartsWith("reference.", StringComparison.Ordinal)
				? capture.Name["reference.".Length..]
				: capture.NodeType,
			Site(context, capture));

	private static FileFacts Failure(DependencyExtractionContext context, string reason) => new(
		context.RelativePath, context.ScopeId, context.LanguageId, context.ContentFingerprint,
		context.Source.Length, DependencyFileStatus.ExtractionFailed, reason, context.HasSyntaxErrors,
		context.ErrorNodeKinds, [], [], [], [], new Dictionary<string, string>(), [], new Dictionary<string, string>(), []);

	private static IReadOnlyList<NamespaceSpan> ParseNamespaces(IEnumerable<DependencySyntaxCapture> captures) =>
		captures.Where(static capture => capture.Name == "context.namespace")
			.Select(static capture => new NamespaceSpan(capture.CapturedName ?? string.Empty,
				capture.StartIndex, capture.EndIndex, capture.NodeType == "file_scoped_namespace_declaration"))
			.Where(static item => item.Name.Length > 0).ToArray();

	private static IReadOnlyDictionary<string, string> ParseUsings(
		IEnumerable<DependencySyntaxCapture> captures,
		out HashSet<string> namespaces,
		out HashSet<string> globalNamespaces,
		out IReadOnlyDictionary<string, string> globalAliases)
	{
		namespaces = new HashSet<string>(StringComparer.Ordinal);
		globalNamespaces = new HashSet<string>(StringComparer.Ordinal);
		var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
		var globals = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var capture in captures.Where(static capture => capture.Name == "context.using"))
		{
			var match = UsingRegex().Match(capture.Text);
			if (!match.Success || match.Groups["static"].Success)
				continue;
			var target = match.Groups["target"].Value.Replace("global::", string.Empty, StringComparison.Ordinal);
			var isGlobal = capture.Text.TrimStart().StartsWith("global using ", StringComparison.Ordinal);
			if (match.Groups["alias"].Success)
				(isGlobal ? globals : aliases)[match.Groups["alias"].Value] = target;
			else
				(isGlobal ? globalNamespaces : namespaces).Add(target);
		}
		globalAliases = globals;
		return aliases;
	}

	private static string SimpleName(string qualified)
	{
		var value = qualified[(qualified.LastIndexOf('.') + 1)..];
		var arity = value.IndexOf('`');
		return arity < 0 ? value : value[..arity];
	}
	private static string AritySuffix(int arity) => arity == 0 ? string.Empty : $"`{arity}";
	private sealed record NamespaceSpan(string Name, int Start, int End, bool FileScoped);
	private static readonly HashSet<string> Keywords = new(
		["public", "private", "protected", "internal", "static", "readonly", "ref", "out", "in", "params", "this", "where", "new", "class", "struct", "interface", "record", "enum", "delegate", "void", "var", "get", "set", "init", "return", "true", "false", "null"],
		StringComparer.Ordinal);

	[GeneratedRegex(@"\b(?:global\s+)?using\s+(?<static>static\s+)?(?:(?<alias>[A-Za-z_]\w*)\s*=\s*)?(?<target>(?:global::)?[A-Za-z_]\w*(?:(?:\.|::)[A-Za-z_]\w*)*)\s*;", RegexOptions.CultureInvariant)] private static partial Regex UsingRegex();
	[GeneratedRegex(@"(?<name>[A-Za-z_]\w*)", RegexOptions.CultureInvariant)] private static partial Regex TypeParameterRegex();
	[GeneratedRegex(@"(?:global::)?[A-Za-z_]\w*(?:(?:\.|::)[A-Za-z_]\w*)*", RegexOptions.CultureInvariant)] private static partial Regex TypeNameRegex();
}

internal sealed partial class TypeScriptDependencyLanguageAdapter : DependencyLanguageAdapter
{
	public override FileFacts Extract(DependencyExtractionContext context, DependencyFactsLimits limits)
	{
		var module = Path.ChangeExtension(context.RelativePath, null)!.Replace('\\', '/');
		var declarations = context.Declarations.Select(capture => ToDeclaration(context, capture, module))
			.Where(static item => item is not null).Cast<DeclarationFact>().ToArray();
		var imports = context.References.Where(static capture => capture.Name.StartsWith("import.", StringComparison.Ordinal))
			.SelectMany(capture => ExtractImports(context, capture)).ToArray();
		var references = Distinct(context.References.Where(static capture => capture.Name.StartsWith("reference.", StringComparison.Ordinal))
			.SelectMany(capture => ExtractTypes(context, capture)));
		if (declarations.Length + imports.Length + references.Count > limits.MaximumFactsPerFile)
			return Failed(context);
		return Complete(context, declarations, imports, references);
	}

	private static DeclarationFact? ToDeclaration(DependencyExtractionContext context, DependencySyntaxCapture capture, string module)
	{
		if (string.IsNullOrEmpty(capture.CapturedName)) return null;
		var kind = capture.Name switch
		{
			"declaration.class" => SymbolKind.Class,
			"declaration.interface" => SymbolKind.Interface,
			"declaration.enum" => SymbolKind.Enum,
			"declaration.function" => SymbolKind.Function,
			"declaration.module" => SymbolKind.Module,
			_ => SymbolKind.Record
		};
		return new DeclarationFact(new SymbolIdentity(context.ScopeId, context.LanguageId, kind,
			$"{module}#{capture.CapturedName}", capture.GenericArity), [Site(context, capture)]);
	}

	private static IEnumerable<ImportFact> ExtractImports(DependencyExtractionContext context, DependencySyntaxCapture capture)
	{
		if (capture.Name == "import.call")
		{
			var require = RequireRegex().Match(capture.Text);
			if (require.Success)
				yield return new ImportFact(require.Groups["path"].Value, null, null, false, 0, Site(context, capture));
			yield break;
		}
		var match = ModuleRegex().Match(capture.Text);
		if (!match.Success) yield break;
		var specifier = match.Groups["path"].Value;
		var names = NamedImportsRegex().Match(capture.Text);
		if (!names.Success)
		{
			yield return new ImportFact(specifier, null, null, capture.Text.Contains('*'), 0, Site(context, capture));
			yield break;
		}
		foreach (var item in names.Groups["names"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var parts = Regex.Split(item, @"\s+as\s+", RegexOptions.CultureInvariant);
			yield return new ImportFact(specifier, parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : null,
				false, 0, Site(context, capture));
		}
	}

	private static IEnumerable<ReferenceFact> ExtractTypes(DependencyExtractionContext context, DependencySyntaxCapture capture)
	{
		var candidate = capture.Name == "reference.new" ? NewTypeRegex().Match(capture.Text).Groups["type"].Value : capture.Text;
		foreach (Match match in TypeRegex().Matches(candidate))
		{
			var value = match.Value;
			if (!Keywords.Contains(value))
				yield return new ReferenceFact(EvidenceLayer.TypeReference, value.Split('.').Last(),
					GenericArityAt(candidate, match.Index + match.Length), capture.NodeType, Site(context, capture));
		}
	}

	private static FileFacts Failed(DependencyExtractionContext context) => new(
		context.RelativePath, context.ScopeId, context.LanguageId, context.ContentFingerprint, context.Source.Length,
		DependencyFileStatus.ExtractionFailed, "fact limit exceeded", context.HasSyntaxErrors, context.ErrorNodeKinds,
		[], [], [], [], new Dictionary<string, string>(), [], new Dictionary<string, string>(), []);
	private static readonly HashSet<string> Keywords = new(
		["string", "number", "boolean", "unknown", "never", "any", "void", "null", "undefined", "keyof", "typeof", "readonly", "new", "extends", "implements"], StringComparer.Ordinal);
	[GeneratedRegex("(?:from\\s+|import\\s*\\(|require\\s*\\()\\s*['\"](?<path>[^'\"]+)['\"]", RegexOptions.CultureInvariant)] private static partial Regex ModuleRegex();
	[GeneratedRegex("""require\s*\(\s*['"](?<path>[^'"]+)['"]\s*\)""", RegexOptions.CultureInvariant)] private static partial Regex RequireRegex();
	[GeneratedRegex(@"\{(?<names>[^}]+)\}", RegexOptions.CultureInvariant)] private static partial Regex NamedImportsRegex();
	[GeneratedRegex(@"[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*", RegexOptions.CultureInvariant)] private static partial Regex TypeRegex();
	[GeneratedRegex(@"\bnew\s+(?<type>[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*)", RegexOptions.CultureInvariant)] private static partial Regex NewTypeRegex();
}

internal sealed partial class PythonDependencyLanguageAdapter : DependencyLanguageAdapter
{
	public override FileFacts Extract(DependencyExtractionContext context, DependencyFactsLimits limits)
	{
		var module = Path.ChangeExtension(context.RelativePath, null)!.Replace('/', '.').Replace('\\', '.');
		if (module.EndsWith(".__init__", StringComparison.Ordinal)) module = module[..^".__init__".Length];
		var declarations = context.Declarations.Select(capture =>
		{
			if (string.IsNullOrEmpty(capture.CapturedName)) return null;
			var kind = capture.Name == "declaration.class" ? SymbolKind.Class : SymbolKind.Function;
			return new DeclarationFact(new SymbolIdentity(context.ScopeId, context.LanguageId, kind,
				$"{module}.{capture.CapturedName}", 0), [Site(context, capture)]);
		}).Where(static item => item is not null).Cast<DeclarationFact>().ToArray();
		var imports = context.References.Where(static capture => capture.Name.StartsWith("import.", StringComparison.Ordinal))
			.SelectMany(capture => ExtractImports(context, capture)).ToArray();
		var references = Distinct(context.References.Where(static capture => capture.Name.StartsWith("reference.", StringComparison.Ordinal))
			.SelectMany(capture => TypeRegex().Matches(capture.Text).Select(static match => match.Value)
				.Where(value => !Keywords.Contains(value))
				.Select(value => new ReferenceFact(EvidenceLayer.TypeReference, value.Split('.').Last(), 0,
					capture.NodeType, Site(context, capture)))));
		if (declarations.Length + imports.Length + references.Count > limits.MaximumFactsPerFile)
			return Failed(context);
		var metadata = DynamicAllRegex().IsMatch(context.Source)
			? new Dictionary<string, string>(StringComparer.Ordinal) { ["$dynamic-all"] = "true" }
			: new Dictionary<string, string>(StringComparer.Ordinal);
		return Complete(context, declarations, imports, references, aliases: metadata);
	}

	private static IEnumerable<ImportFact> ExtractImports(DependencyExtractionContext context, DependencySyntaxCapture capture)
	{
		if (capture.Name == "import.direct")
		{
			foreach (var item in capture.Text["import".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var parts = Regex.Split(item, @"\s+as\s+", RegexOptions.CultureInvariant);
				yield return new ImportFact(parts[0], null, parts.Length > 1 ? parts[1] : null, false, 0, Site(context, capture));
			}
			yield break;
		}
		var match = FromRegex().Match(capture.Text);
		if (!match.Success) yield break;
		var dotted = match.Groups["module"].Value;
		var relativeLevel = dotted.TakeWhile(static value => value == '.').Count();
		var module = dotted[relativeLevel..];
		foreach (var item in match.Groups["names"].Value.Trim('(', ')', ' ').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var parts = Regex.Split(item, @"\s+as\s+", RegexOptions.CultureInvariant);
			yield return new ImportFact(module, parts[0], parts.Length > 1 ? parts[1] : null,
				parts[0] == "*", relativeLevel, Site(context, capture));
		}
	}

	private static FileFacts Failed(DependencyExtractionContext context) => new(
		context.RelativePath, context.ScopeId, context.LanguageId, context.ContentFingerprint, context.Source.Length,
		DependencyFileStatus.ExtractionFailed, "fact limit exceeded", context.HasSyntaxErrors, context.ErrorNodeKinds,
		[], [], [], [], new Dictionary<string, string>(), [], new Dictionary<string, string>(), []);
	private static readonly HashSet<string> Keywords = new(
		["def", "class", "None", "True", "False", "str", "int", "float", "bool", "bytes", "list", "dict", "tuple", "set", "object", "typing", "self", "cls"], StringComparer.Ordinal);
	[GeneratedRegex(@"^from\s+(?<module>\.*(?:[A-Za-z_][\w.]*)?)\s+import\s+(?<names>.+)$", RegexOptions.CultureInvariant)] private static partial Regex FromRegex();
	[GeneratedRegex(@"[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*", RegexOptions.CultureInvariant)] private static partial Regex TypeRegex();
	[GeneratedRegex(@"(?m)^\s*__all__\s*=\s*[A-Za-z_]", RegexOptions.CultureInvariant)] private static partial Regex DynamicAllRegex();
}
