namespace DevProjex.Application.Dependencies;

public sealed partial class DependencyFactsEngine
{
	private sealed partial class ResolverContext
	{
		private DependencyEdge ResolveScalaImport(FileFacts source, ImportFact import)
		{
			if (ScalaConfigurationFailure(source) is { } failure)
				return Edge(source, import, ResolutionStatus.Unresolved, null, failure, []);
			if (import.IsWildcard)
				return Edge(source, import, ResolutionStatus.Unresolved, null, "wildcard import is resolution context, not a dependency target", []);
			if (!import.Specifier.StartsWith("_root_.", StringComparison.Ordinal) && source.ScalaValueScopes.Any(scope =>
				(scope.Name == import.Specifier.Split('.')[0] || scope.Name == "$unknown-binding") && scope.StartLine <= import.Site.Line && scope.EndLine >= import.Site.Line))
				return Edge(source, import, ResolutionStatus.Unresolved, null, "Scala import qualifier binding is not proven", []);
			return ScalaImportCandidates(source, import, LookupScalaImport(source, import.Specifier, import.ContainingDeclaration ?? string.Empty));
		}

		private DeclarationFact[] LookupScalaImport(FileFacts source, string name, string namespaceName)
		{
			if (name.StartsWith("_root_.", StringComparison.Ordinal)) return LookupQualified(source, name[7..], 0);
			while (namespaceName.Length > 0)
			{
				var local = FilterScalaCandidates(source, LookupQualified(source, namespaceName + "." + name, 0));
				if (local.Length > 0) return local;
				var separator = namespaceName.LastIndexOf('.');
				namespaceName = separator < 0 ? string.Empty : namespaceName[..separator];
			}
			return LookupQualified(source, name, 0);
		}

		private string? ScalaConfigurationFailure(FileFacts source)
		{
			var scope = FindScope(source.ScopeId);
			return scope?.HasConfiguration == true ? ConfigurationFailure(scope) : "no owning build.sbt or build.sc in the manifest";
		}

		private DependencyEdge ScalaImportCandidates(FileFacts source, ImportFact import, DeclarationFact[] declarations)
		{
			declarations = FilterScalaCandidates(source, declarations);
			var targets = declarations.SelectMany(static declaration => declaration.DeclarationSites).Select(static site => site.File)
				.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
			return FinishImport(source, import, targets, "one imported declaration");
		}

		private DependencyEdge ResolveScalaType(FileFacts source, ReferenceFact reference)
		{
			if (ScalaConfigurationFailure(source) is { } failure)
				return Edge(source, reference, ResolutionStatus.Unresolved, null, failure, []);
			if (source.TypeParameterScopes.Any(parameter => parameter.Name == reference.Name &&
				parameter.StartIndex <= reference.SourceStartIndex && parameter.EndIndex >= reference.SourceStartIndex))
				return Edge(source, reference, ResolutionStatus.Unresolved, null, "type parameter shadows declarations", []);
			var activeImports = source.ScalaImportDirectives.Where(directive =>
				directive.ScopeStartIndex <= reference.SourceStartIndex && directive.ScopeEndIndex >= reference.SourceStartIndex).ToArray();
			if (activeImports.Any(static directive => directive.IsWildcard))
				return Edge(source, reference, ResolutionStatus.Unresolved, null, "Scala wildcard binding is not proven", []);
			var qualified = reference.Name.Contains('.');
			DeclarationFact[] candidates = [];
			var head = reference.Name.Split('.')[0];
			if (source.ScalaValueScopes.Any(scope => (scope.Name == head || scope.Name == "$unknown-binding") &&
				scope.StartIndex <= reference.SourceStartIndex && scope.EndIndex >= reference.SourceStartIndex))
				return Edge(source, reference, ResolutionStatus.Unresolved, null, "Scala type qualifier binding is not proven", []);
			var imports = activeImports.Where(directive => (directive.Alias ?? directive.Specifier.Split('.')[^1]) == head).ToArray();
			if (source.Declarations.Any(declaration => declaration.Identity.FileScope == source.Path &&
				declaration.Identity.SymbolKind is SymbolKind.Class or SymbolKind.Interface && declaration.Identity.QualifiedName.Split('.')[^1] == head))
				return Edge(source, reference, ResolutionStatus.Unresolved, null, "Scala local type binding is not proven", []);
			if (imports.Length > 0)
			{
				if (imports.Length > 1)
					return Edge(source, reference, ResolutionStatus.Unresolved, null, "Scala import binding is not proven", []);
				if (imports.Any(directive => !directive.Specifier.StartsWith("_root_.", StringComparison.Ordinal) && source.ScalaValueScopes.Any(scope =>
					(scope.Name == directive.Specifier.Split('.')[0] || scope.Name == "$unknown-binding") && scope.StartIndex <= directive.ScopeStartIndex && scope.EndIndex >= directive.ScopeStartIndex)))
					return Edge(source, reference, ResolutionStatus.Unresolved, null, "Scala import qualifier binding is not proven", []);
				candidates = imports.SelectMany(directive => LookupScalaImport(source,
					directive.Specifier + reference.Name[head.Length..], directive.ContainingNamespace)).ToArray();
			}
			else
			{
				if (qualified)
					return Edge(source, reference, ResolutionStatus.Unresolved, null, "Scala qualified type binding is not proven", []);
				var owner = reference.ContainingType;
				while (!string.IsNullOrEmpty(owner) && candidates.Length == 0)
				{
					candidates = FilterScalaCandidates(source, LookupQualified(source, owner + "." + reference.Name, 0));
					var separator = owner.LastIndexOf('.');
					owner = separator < 0 ? null : owner[..separator];
				}
				if (candidates.Length == 0 && reference.ContainingNamespace.Length > 0)
					candidates = LookupQualified(source, reference.ContainingNamespace + "." + reference.Name, 0);
				if (candidates.Length == 0 && reference.ContainingNamespace.Length == 0)
					candidates = LookupQualified(source, reference.Name, 0);
			}
			candidates = FilterScalaCandidates(source, candidates).Where(static declaration =>
				declaration.Identity.SymbolKind is SymbolKind.Class or SymbolKind.Interface or SymbolKind.Module).ToArray();
			var targets = candidates.SelectMany(static declaration => declaration.DeclarationSites).Select(static site => site.File)
				.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
			return targets.Length switch
			{
				0 => Edge(source, reference, ResolutionStatus.Unresolved, null, "no visible declaration in the manifest", []),
				1 => Edge(source, reference, ResolutionStatus.Resolved, targets[0], "one visible declaration", targets),
				_ => Edge(source, reference, ResolutionStatus.Ambiguous, null, "multiple visible declarations", targets)
			};
		}

		private static DeclarationFact[] FilterScalaCandidates(FileFacts source, IEnumerable<DeclarationFact> candidates) =>
			candidates.Where(declaration => (declaration.Identity.FileScope is null ||
				declaration.Identity.FileScope == source.Path && declaration.Identity.SymbolKind is SymbolKind.Class or SymbolKind.Interface) &&
				(!source.Path.Contains("/src/main/scala/", StringComparison.Ordinal) && !source.Path.StartsWith("src/main/scala/", StringComparison.Ordinal) ||
				 !declaration.DeclarationSites.Any(static site => site.File.Contains("/src/test/scala/", StringComparison.Ordinal) || site.File.StartsWith("src/test/scala/", StringComparison.Ordinal))))
				.Distinct().ToArray();
	}
}
