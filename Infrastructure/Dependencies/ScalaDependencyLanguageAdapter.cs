using DevProjex.Application.Dependencies;

namespace DevProjex.Infrastructure.Dependencies;

internal sealed class ScalaDependencyLanguageAdapter : DependencyLanguageAdapter
{
	private static readonly IReadOnlyDictionary<string, SymbolKind> Kinds = new Dictionary<string, SymbolKind>(StringComparer.Ordinal)
	{
		["declaration.scala_package"] = SymbolKind.Module,
		["declaration.scala_class"] = SymbolKind.Class,
		["declaration.scala_object"] = SymbolKind.Module,
		["declaration.scala_trait"] = SymbolKind.Interface,
		["declaration.scala_function"] = SymbolKind.Function,
		["declaration.scala_val"] = SymbolKind.Function
	};

	public override FileFacts Extract(DependencyExtractionContext context, DependencyFactsLimits limits)
	{
		var declarations = context.Declarations.Where(capture => Kinds.ContainsKey(capture.Name) && capture.CapturedName is not null)
			.Select(capture =>
			{
				var parts = new[] { capture.LexicalNamespace, capture.ContainingDeclaration, capture.CapturedName }.Where(static value => !string.IsNullOrEmpty(value));
				return new DeclarationFact(new SymbolIdentity(context.ScopeId, context.LanguageId, Kinds[capture.Name],
					string.Join('.', parts), 0, capture.Name == "declaration.scala_package" || capture.IsFileLocal ? context.RelativePath : null), [Site(context, capture)])
				{
					ContainingNamespace = capture.LexicalNamespace ?? string.Empty,
					ContainingType = string.IsNullOrEmpty(capture.ContainingDeclaration) ? null :
						string.Join('.', new[] { capture.LexicalNamespace, capture.ContainingDeclaration }.Where(static value => !string.IsNullOrEmpty(value)))
				};
			}).ToArray();
		var imports = context.References.Where(static capture => capture.Name == "import.scala_import" && capture.ImportSyntax is not null)
			.SelectMany(capture => capture.ImportSyntax!.HasLiteralSpecifier
				? capture.ImportSyntax.Bindings.Select(binding => new ImportFact(
					string.Join('.', new[] { capture.ImportSyntax.Specifier, binding.IsWildcard ? null : binding.Name }.Where(static value => !string.IsNullOrEmpty(value))),
					binding.Name, binding.Alias, binding.IsWildcard, 0, Site(context, capture),
					Reason: binding.Alias is "_" ? "Scala excluded import is not a dependency target" : "not resolved yet")
				{
					ContainingDeclaration = capture.ContainingDeclaration
				})
				: [new ImportFact(string.Empty, null, null, false, 0, Site(context, capture), Reason: "Scala import syntax is not supported")])
			.ToArray();
		var references = Distinct(context.References.Where(static capture => capture.Name == "reference.scala_type")
			.Select(capture => new ReferenceFact(EvidenceLayer.TypeReference, capture.Text, 0, capture.NodeType, Site(context, capture))
			{
				ContainingNamespace = capture.LexicalNamespace ?? string.Empty,
				ContainingType = string.IsNullOrEmpty(capture.ContainingDeclaration) ? null :
					string.Join('.', new[] { capture.LexicalNamespace, capture.ContainingDeclaration }.Where(static value => !string.IsNullOrEmpty(value))),
				SourceStartIndex = capture.StartIndex
			}));
		if (declarations.Length + imports.Length + references.Count > limits.MaximumFactsPerFile)
			return Complete(context, [], [], []) with { Status = DependencyFileStatus.ExtractionFailed, StatusReason = "fact limit exceeded" };
		return Complete(context, declarations, imports, references) with
		{
			ScalaImportDirectives = context.References.Where(static capture => capture.Name == "import.scala_import" && capture.ImportSyntax?.HasLiteralSpecifier == true)
				.SelectMany(capture => capture.ImportSyntax!.Bindings.Where(static binding => binding.Alias != "_").Select(binding => new ScalaImportDirective(
					string.Join('.', new[] { capture.ImportSyntax.Specifier, binding.IsWildcard ? null : binding.Name }.Where(static value => !string.IsNullOrEmpty(value))),
					binding.Alias, binding.IsWildcard, capture.ScopeStartIndex, capture.ScopeEndIndex)
				{
					ContainingNamespace = capture.ContainingDeclaration ?? string.Empty
				})).ToArray(),
			ScalaValueScopes = context.References.Where(static capture => capture.Name == "context.scala_value")
				.Select(static capture => new ScalaValueScope(capture.Text, capture.StartIndex, capture.EndIndex, capture.Line, capture.EndLine)).ToArray(),
			TypeParameterScopes = context.References.Where(static capture => capture.Name == "context.scala_parameter")
				.Select(static capture => new TypeParameterScope(capture.Text, capture.StartIndex, capture.EndIndex)).ToArray()
		};
	}
}
