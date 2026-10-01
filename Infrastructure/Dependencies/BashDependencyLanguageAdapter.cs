using DevProjex.Application.Dependencies;

namespace DevProjex.Infrastructure.Dependencies;

internal sealed class BashDependencyLanguageAdapter : DependencyLanguageAdapter
{
	public override FileFacts Extract(DependencyExtractionContext context, DependencyFactsLimits limits)
	{
		var declarations = context.Declarations
			.Where(static capture => capture.Name == "declaration.bash_function" && capture.CapturedName is not null)
			.Select(capture => new DeclarationFact(new SymbolIdentity(context.ScopeId, context.LanguageId,
				SymbolKind.Function, capture.CapturedName!, 0, context.RelativePath), [Site(context, capture)]))
			.ToArray();
		var imports = context.References.Where(static capture => capture.Name == "import.bash" && capture.ImportSyntax is not null)
			.Select(capture => new ImportFact(capture.ImportSyntax!.Specifier, capture.ImportSyntax.Bindings.Single().Name,
				null, false, 0, Site(context, capture),
				Reason: capture.ImportSyntax.HasLiteralSpecifier ? "not resolved yet" : "Bash path expansion is not supported"))
			.ToArray();
		if (declarations.Length + imports.Length > limits.MaximumFactsPerFile)
			return Complete(context, [], [], []) with { Status = DependencyFileStatus.ExtractionFailed, StatusReason = "fact limit exceeded" };
		return Complete(context, declarations, imports, []);
	}
}
