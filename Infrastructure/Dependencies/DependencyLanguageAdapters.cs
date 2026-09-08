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
	bool IsFileLocal = false,
	string? ContainingDeclaration = null,
	DependencyImportSyntax? ImportSyntax = null,
	int CapturedNameStartIndex = -1,
	string? Evidence = null);

internal sealed record DependencyImportSyntax(
	string Specifier,
	int RelativeLevel,
	IReadOnlyList<DependencyImportBinding> Bindings,
	bool HasLiteralSpecifier = true,
	ModuleImportKind ImportKind = ModuleImportKind.StaticImport);

internal sealed record DependencyImportBinding(string Name, string? Alias, bool IsWildcard = false);

internal sealed record DependencyExtractionContext(
	string RelativePath,
	string ScopeId,
	LanguageId LanguageId,
	string Source,
	string ContentFingerprint,
	bool HasSyntaxErrors,
	IReadOnlyDictionary<string, int> ErrorNodeKinds,
	IReadOnlyList<DependencySyntaxCapture> Declarations,
	IReadOnlyList<DependencySyntaxCapture> References)
{
	public DependencyAdapterWorkCounter Work { get; } = new();
}

internal sealed class DependencyAdapterWorkCounter
{
	public long VisitedRanges { get; private set; }
	public long Comparisons { get; private set; }

	public void VisitRange() => VisitedRanges++;
	public void Compare() => Comparisons++;
}

internal interface IDependencyLanguageAdapter
{
	FileFacts Extract(DependencyExtractionContext context, DependencyFactsLimits limits);
}

internal abstract partial class DependencyLanguageAdapter : IDependencyLanguageAdapter
{
	protected static SourceSite Site(DependencyExtractionContext context, DependencySyntaxCapture capture) =>
		new(context.RelativePath, capture.Line, capture.Evidence ?? OneLine(capture.Text));

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
			(fact.Layer, fact.Name, fact.GenericArity, fact.Site.Line, fact.SyntaxKind,
				fact.SourceStartIndex, fact.ContainingNamespace, fact.ContainingType, fact.IsGlobalQualified))
			.Select(static group => group.First())
			.OrderBy(static fact => fact.Site.Line)
			.ThenBy(static fact => fact.SourceStartIndex)
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
		var usingDirectives = ParseUsings(
			context.Declarations,
			namespaces,
			context.Source.Length,
			out var aliases,
			out var usingNamespaces,
			out var globalNamespaces,
			out var globalAliases);
		var typeParameterScopes = context.References
			.Where(static capture => capture.Name == "context.type_parameters")
			.SelectMany(static capture => TypeParameterRegex().Matches(capture.Text)
				.Select(match => new TypeParameterScope(
					match.Groups["name"].Value,
					capture.StartIndex,
					capture.EndIndex)))
			.Distinct().OrderBy(static scope => scope.StartIndex)
			.ThenBy(static scope => scope.Name, StringComparer.Ordinal).ToArray();
		var typeParameters = typeParameterScopes.Select(static scope => scope.Name)
			.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
		var declarationCaptures = context.Declarations
			.Where(capture => Kinds.ContainsKey(capture.Name) && !string.IsNullOrEmpty(capture.CapturedName))
			.ToArray();
		var declarationScopes = BuildDeclarationScopes(declarationCaptures, namespaces, context.Work);
		var declarations = new List<DeclarationFact>(declarationCaptures.Length);
		foreach (var capture in declarationCaptures)
		{
			var declarationScope = declarationScopes.ByCapture[capture];
			declarations.Add(new DeclarationFact(
				new SymbolIdentity(
					context.ScopeId,
					context.LanguageId,
					Kinds[capture.Name],
					declarationScope.QualifiedName,
					capture.GenericArity,
					capture.IsFileLocal ? context.RelativePath : null),
				[Site(context, capture)])
			{
				ContainingNamespace = declarationScope.ContainingNamespace,
				ContainingType = declarationScope.ContainingType
			});
		}

		var referenceCaptures = context.References
			.Where(static capture => capture.Name.StartsWith("reference.", StringComparison.Ordinal))
			.ToArray();
		var referenceScopes = BuildReferenceScopes(referenceCaptures, declarationScopes.Ordered, context.Work);
		var declarationOccurrences = declarationCaptures
			.Where(static capture => capture.CapturedNameStartIndex >= 0)
			.Select(static capture => (
				capture.CapturedNameStartIndex,
				Name: capture.CapturedName!))
			.ToHashSet();
		var references = referenceCaptures
			.SelectMany(capture => ExtractReferences(
				context,
				capture,
				referenceScopes.GetValueOrDefault(capture),
				namespaces))
			.Where(reference => !declarationOccurrences.Contains((reference.SourceStartIndex, reference.Name)))
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
			typeParameters) with
		{
			TypeParameterScopes = typeParameterScopes,
			CSharpUsingDirectives = usingDirectives
		};
	}

	private static IEnumerable<ReferenceFact> ExtractReferences(
		DependencyExtractionContext context,
		DependencySyntaxCapture capture,
		DeclarationScope? declarationScope,
		IReadOnlyList<NamespaceSpan> namespaces)
	{
		var containingNamespace = declarationScope?.ContainingNamespace ??
			FindContainingNamespace(capture, namespaces, context.Work);
		var containingType = declarationScope?.QualifiedName;
		if (capture.Name == "reference.target_typed_object_creation")
		{
			yield return NewReference(context, capture, "<target-typed-new>", 0, containingNamespace, containingType);
			yield break;
		}
		var typeText = capture.Text;
		foreach (Match match in TypeNameRegex().Matches(typeText))
		{
			var isGlobalQualified = match.Value.StartsWith("global::", StringComparison.Ordinal);
			var name = match.Value.Replace("global::", string.Empty, StringComparison.Ordinal)
				.Replace("::", ".", StringComparison.Ordinal);
			var simpleName = name.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? name;
			if (!Keywords.Contains(simpleName))
					yield return NewReference(
					context,
					capture,
					name,
					GenericArityAt(typeText, match.Index + match.Length),
					containingNamespace,
					containingType,
					isGlobalQualified);
		}
	}

	private static DeclarationScopeIndex BuildDeclarationScopes(
		IReadOnlyList<DependencySyntaxCapture> declarations,
		IReadOnlyList<NamespaceSpan> namespaces,
		DependencyAdapterWorkCounter work)
	{
		var byCapture = new Dictionary<DependencySyntaxCapture, DeclarationScope>(ReferenceEqualityComparer.Instance);
		var orderedScopes = new List<DeclarationScope>(declarations.Count);
		var active = new Stack<DeclarationScope>();
		foreach (var capture in declarations
			         .OrderBy(static item => item.StartIndex)
			         .ThenByDescending(static item => item.EndIndex)
			         .ThenBy(static item => item.CapturedName, StringComparer.Ordinal))
		{
			work.VisitRange();
			while (active.TryPeek(out var current) && !Contains(current.Capture, capture, work))
				active.Pop();
			var containingNamespace = FindContainingNamespace(capture, namespaces, work);
			var containingType = active.TryPeek(out var parent) ? parent.QualifiedName : null;
			var qualifiedName = string.Join('.', new[]
				{
					containingType ?? containingNamespace,
					capture.CapturedName + AritySuffix(capture.GenericArity)
				}.Where(static value => value.Length > 0));
			var scope = new DeclarationScope(capture, containingNamespace, qualifiedName, containingType);
			byCapture.Add(capture, scope);
			orderedScopes.Add(scope);
			active.Push(scope);
		}
		return new DeclarationScopeIndex(byCapture, orderedScopes);
	}

	private static IReadOnlyDictionary<DependencySyntaxCapture, DeclarationScope?> BuildReferenceScopes(
		IReadOnlyList<DependencySyntaxCapture> references,
		IReadOnlyList<DeclarationScope> declarations,
		DependencyAdapterWorkCounter work)
	{
		var result = new Dictionary<DependencySyntaxCapture, DeclarationScope?>(ReferenceEqualityComparer.Instance);
		var active = new Stack<DeclarationScope>();
		var declarationIndex = 0;
		foreach (var reference in references
			         .OrderBy(static item => item.StartIndex)
			         .ThenBy(static item => item.EndIndex))
		{
			work.VisitRange();
			while (declarationIndex < declarations.Count &&
			       declarations[declarationIndex].Capture.StartIndex < reference.StartIndex)
			{
				var declaration = declarations[declarationIndex++];
				while (active.TryPeek(out var current) && !Contains(current.Capture, declaration.Capture, work))
					active.Pop();
				active.Push(declaration);
			}
			while (active.TryPeek(out var current) && !Contains(current.Capture, reference, work))
				active.Pop();
			result[reference] = active.TryPeek(out var containing) ? containing : null;
		}
		return result;
	}

	private static bool Contains(
		DependencySyntaxCapture container,
		DependencySyntaxCapture item,
		DependencyAdapterWorkCounter work)
	{
		work.Compare();
		return container.StartIndex < item.StartIndex && container.EndIndex >= item.EndIndex;
	}

	private static string FindContainingNamespace(
		DependencySyntaxCapture capture,
		IReadOnlyList<NamespaceSpan> namespaces,
		DependencyAdapterWorkCounter work)
	{
		NamespaceSpan? closest = null;
		foreach (var item in namespaces)
		{
			work.VisitRange();
			work.Compare();
			if (item.Start > capture.StartIndex || item.End < capture.EndIndex) continue;
			if (closest is null || item.End - item.Start < closest.End - closest.Start) closest = item;
		}
		return closest?.Name ?? namespaces.FirstOrDefault(static item => item.FileScoped)?.Name ?? string.Empty;
	}

	private static ReferenceFact NewReference(
		DependencyExtractionContext context,
		DependencySyntaxCapture capture,
		string name,
		int arity,
		string containingNamespace,
		string? containingType,
		bool isGlobalQualified = false) =>
		new(EvidenceLayer.TypeReference, name, arity,
			capture.Name.StartsWith("reference.", StringComparison.Ordinal)
				? capture.Name["reference.".Length..]
				: capture.NodeType,
			Site(context, capture))
		{
			ContainingNamespace = containingNamespace,
			ContainingType = containingType,
			SourceStartIndex = capture.StartIndex,
			IsGlobalQualified = isGlobalQualified
		};

	private static FileFacts Failure(DependencyExtractionContext context, string reason) => new(
		context.RelativePath, context.ScopeId, context.LanguageId, context.ContentFingerprint,
		context.Source.Length, DependencyFileStatus.ExtractionFailed, reason, context.HasSyntaxErrors,
		context.ErrorNodeKinds, [], [], [], [], new Dictionary<string, string>(), [], new Dictionary<string, string>(), []);

	private static IReadOnlyList<NamespaceSpan> ParseNamespaces(IEnumerable<DependencySyntaxCapture> captures) =>
		captures.Where(static capture => capture.Name == "context.namespace")
			.Select(static capture => new NamespaceSpan(capture.CapturedName ?? string.Empty,
				capture.StartIndex, capture.EndIndex, capture.NodeType == "file_scoped_namespace_declaration"))
			.Where(static item => item.Name.Length > 0).ToArray();

	private static IReadOnlyList<CSharpUsingDirective> ParseUsings(
		IEnumerable<DependencySyntaxCapture> captures,
		IReadOnlyList<NamespaceSpan> namespaceSpans,
		int sourceLength,
		out IReadOnlyDictionary<string, string> fileAliases,
		out HashSet<string> namespaces,
		out HashSet<string> globalNamespaces,
		out IReadOnlyDictionary<string, string> globalAliases)
	{
		namespaces = new HashSet<string>(StringComparer.Ordinal);
		globalNamespaces = new HashSet<string>(StringComparer.Ordinal);
		var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
		var globals = new Dictionary<string, string>(StringComparer.Ordinal);
		var directives = new List<CSharpUsingDirective>();
		foreach (var capture in captures.Where(static capture => capture.Name == "context.using"))
		{
			var match = UsingRegex().Match(capture.Text);
			if (!match.Success || match.Groups["static"].Success)
				continue;
			var target = match.Groups["target"].Value.Replace("global::", string.Empty, StringComparison.Ordinal);
			var isGlobal = capture.Text.TrimStart().StartsWith("global using ", StringComparison.Ordinal);
			var alias = match.Groups["alias"].Success ? match.Groups["alias"].Value : null;
			if (isGlobal)
			{
				if (alias is null) globalNamespaces.Add(target);
				else globals[alias] = target;
				continue;
			}
			var lexicalNamespace = namespaceSpans
				.Where(item => item.Start <= capture.StartIndex && item.End >= capture.EndIndex)
				.OrderBy(item => item.End - item.Start)
				.FirstOrDefault();
			var scopeStart = lexicalNamespace?.Start ?? 0;
			var scopeEnd = lexicalNamespace?.End ?? sourceLength;
			directives.Add(new CSharpUsingDirective(target, alias, scopeStart, scopeEnd));
			if (lexicalNamespace is not null) continue;
			if (alias is null) namespaces.Add(target);
			else aliases[alias] = target;
		}
		fileAliases = aliases;
		globalAliases = globals;
		return directives.OrderBy(static item => item.ScopeStartIndex)
			.ThenBy(static item => item.ScopeEndIndex)
			.ThenBy(static item => item.Alias, StringComparer.Ordinal)
			.ThenBy(static item => item.Target, StringComparer.Ordinal)
			.ToArray();
	}

	private static string SimpleName(string qualified)
	{
		var value = qualified[(qualified.LastIndexOf('.') + 1)..];
		var arity = value.IndexOf('`');
		return arity < 0 ? value : value[..arity];
	}
	private static string AritySuffix(int arity) => arity == 0 ? string.Empty : $"`{arity}";
	private sealed record DeclarationScope(
		DependencySyntaxCapture Capture,
		string ContainingNamespace,
		string QualifiedName,
		string? ContainingType);
	private sealed record DeclarationScopeIndex(
		IReadOnlyDictionary<DependencySyntaxCapture, DeclarationScope> ByCapture,
		IReadOnlyList<DeclarationScope> Ordered);
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
		if (capture.ImportSyntax is { } syntax)
		{
			if (!syntax.HasLiteralSpecifier)
			{
				yield return new ImportFact(
					string.Empty, null, null, false, 0, Site(context, capture),
					Reason: "module specifier is not a string literal")
				{
					ImportKind = syntax.ImportKind
				};
				yield break;
			}
			if (syntax.Bindings.Count == 0)
			{
				yield return new ImportFact(syntax.Specifier, null, null, false, 0, Site(context, capture))
				{
					ImportKind = syntax.ImportKind
				};
				yield break;
			}
			foreach (var binding in syntax.Bindings)
				yield return new ImportFact(syntax.Specifier, binding.Name, binding.Alias,
					binding.IsWildcard, 0, Site(context, capture))
				{
					ImportKind = syntax.ImportKind
				};
			yield break;
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
				$"{module}.{capture.CapturedName}", 0), [Site(context, capture)])
			{
				ContainingType = capture.ContainingDeclaration
			};
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
		if (capture.ImportSyntax is not { } syntax) yield break;
		foreach (var binding in syntax.Bindings)
			yield return (capture.Name == "import.direct"
				? new ImportFact(binding.Name, null, binding.Alias, false, 0, Site(context, capture))
				: new ImportFact(syntax.Specifier, binding.Name, binding.Alias,
					binding.IsWildcard, syntax.RelativeLevel, Site(context, capture))) with
			{
				ContainingDeclaration = capture.ContainingDeclaration
			};
	}

	private static FileFacts Failed(DependencyExtractionContext context) => new(
		context.RelativePath, context.ScopeId, context.LanguageId, context.ContentFingerprint, context.Source.Length,
		DependencyFileStatus.ExtractionFailed, "fact limit exceeded", context.HasSyntaxErrors, context.ErrorNodeKinds,
		[], [], [], [], new Dictionary<string, string>(), [], new Dictionary<string, string>(), []);
	private static readonly HashSet<string> Keywords = new(
		["def", "class", "None", "True", "False", "str", "int", "float", "bool", "bytes", "list", "dict", "tuple", "set", "object", "typing", "self", "cls"], StringComparer.Ordinal);
	[GeneratedRegex(@"[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*", RegexOptions.CultureInvariant)] private static partial Regex TypeRegex();
	[GeneratedRegex(@"(?m)^\s*__all__\s*=\s*[A-Za-z_]", RegexOptions.CultureInvariant)] private static partial Regex DynamicAllRegex();
}
