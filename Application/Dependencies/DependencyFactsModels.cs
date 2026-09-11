namespace DevProjex.Application.Dependencies;

public enum LanguageId
{
	CSharp,
	TypeScript,
	JavaScript,
	Tsx,
	Python,
	Go,
	Java,
	Rust,
	Kotlin,
	Ruby,
	Php,
	Unsupported
}

public enum SymbolKind
{
	Class,
	Struct,
	Interface,
	Record,
	Enum,
	Delegate,
	Function,
	Module
}

public enum NavigationSymbolKind
{
	Type,
	Method,
	Property,
	Field,
	Function,
	Module
}

public sealed record NavigationDeclaration(
	string Name,
	NavigationSymbolKind Kind,
	string? Owner,
	int StartLine,
	int EndLine,
	string ContentFingerprint)
{
	public int StartIndex { get; init; } = -1;
	public int EndIndex { get; init; } = -1;
}

public enum EvidenceLayer
{
	ExplicitImport,
	TypeReference
}

public enum ResolutionStatus
{
	Resolved,
	Ambiguous,
	External,
	Unresolved
}

public enum DependencyFileStatus
{
	Supported,
	Unsupported,
	ExtractionFailed
}

public enum DependencyDirection
{
	Dependencies,
	Dependents,
	Both
}

public sealed record SourceSite(string File, int Line, string Evidence)
{
	/// <summary>
	/// Last line of the declaration this site names, or -1 when the extractor did not report one.
	/// <see cref="Line"/> and this bound together are what let a caller say which declaration a
	/// line belongs to without parsing the file again.
	/// </summary>
	public int EndLine { get; init; } = -1;
}

public sealed record TypeParameterScope(string Name, int StartIndex, int EndIndex);

public sealed record CSharpUsingDirective(
	string Target,
	string? Alias,
	int ScopeStartIndex,
	int ScopeEndIndex);

public sealed record SymbolIdentity(
	string ScopeId,
	LanguageId LanguageId,
	SymbolKind SymbolKind,
	string QualifiedName,
	int GenericArity,
	string? FileScope = null);

public sealed record DeclarationFact(
	SymbolIdentity Identity,
	IReadOnlyList<SourceSite> DeclarationSites)
{
	public string ContainingNamespace { get; init; } = string.Empty;
	public string? ContainingType { get; init; }
}

public enum ModuleImportKind
{
	StaticImport,
	DynamicImport,
	Require
}

public sealed record ImportFact(
	string Specifier,
	string? ImportedName,
	string? Alias,
	bool IsWildcard,
	int RelativeLevel,
	SourceSite Site,
	ResolutionStatus Status = ResolutionStatus.Unresolved,
	string Reason = "not resolved yet",
	IReadOnlyList<string>? Candidates = null,
	string? Target = null)
{
	public string? ContainingDeclaration { get; init; }
	public ModuleImportKind ImportKind { get; init; }
}

public sealed record ReferenceFact(
	EvidenceLayer Layer,
	string Name,
	int GenericArity,
	string SyntaxKind,
	SourceSite Site,
	ResolutionStatus Status = ResolutionStatus.Unresolved,
	string Reason = "not resolved yet",
	IReadOnlyList<string>? Candidates = null,
	string? Target = null)
{
	public string ContainingNamespace { get; init; } = string.Empty;
	public string? ContainingType { get; init; }
	public int SourceStartIndex { get; init; } = -1;
	public bool IsGlobalQualified { get; init; }
}

public sealed record FileFacts(
	string Path,
	string ScopeId,
	LanguageId LanguageId,
	string ContentFingerprint,
	int CharacterCount,
	DependencyFileStatus Status,
	string? StatusReason,
	bool HasSyntaxErrors,
	IReadOnlyDictionary<string, int> ErrorNodeKinds,
	IReadOnlyList<DeclarationFact> Declarations,
	IReadOnlyList<ImportFact> Imports,
	IReadOnlyList<ReferenceFact> References,
	IReadOnlyList<string> ContextNamespaces,
	IReadOnlyDictionary<string, string> Aliases,
	IReadOnlyList<string> GlobalContextNamespaces,
	IReadOnlyDictionary<string, string> GlobalAliases,
	IReadOnlyList<string> TypeParameters)
{
	public bool CanCache { get; init; } = true;
	public IReadOnlyList<NavigationDeclaration> NavigationDeclarations { get; init; } = [];
	public IReadOnlyList<TypeParameterScope> TypeParameterScopes { get; init; } = [];
	public IReadOnlyList<CSharpUsingDirective> CSharpUsingDirectives { get; init; } = [];
}

public sealed record DependencyEdge(
	string Source,
	string? Target,
	EvidenceLayer Layer,
	ResolutionStatus Status,
	string Reference,
	IReadOnlyList<string> Reasons,
	IReadOnlyList<SourceSite> Evidence,
	IReadOnlyList<string> Candidates,
	bool CrossScope)
{
	public IReadOnlyList<string> DeclarationFiles { get; init; } = [];
}

public sealed record DependencyFactsCoverage(
	int Files,
	int Supported,
	int Unsupported,
	int ExtractionFailed,
	IReadOnlyDictionary<string, int> UnsupportedLanguages,
	IReadOnlyDictionary<string, int> CSharpErrorNodeKinds)
{
	public IReadOnlyList<DependencyConfigurationDiagnostic> ConfigurationDiagnostics { get; init; } = [];
}

public sealed record DependencyIndexMetrics(
	int ParsedFiles,
	int ReusedFiles,
	int ReresolvedFiles,
	long ElapsedMilliseconds,
	bool ResolutionCacheHit);

/// <summary>
/// How much an indexing pass learned about one file's bytes while it built the snapshot.
/// </summary>
public enum DependencySourceObservationKind
{
	/// <summary>The pass did not observe the file's content.</summary>
	None,

	/// <summary>The pass read the file's bytes and reported their digest.</summary>
	Read,

	/// <summary>
	/// A preparation cached from an earlier pass was reused, so nothing was read this time.
	/// </summary>
	ReusedPreparation
}

/// <summary>
/// What an indexing pass observed of one file's content. <see cref="ContentDigest"/> is the
/// uppercase hexadecimal SHA-256 of the raw bytes the extractor read, and is present only for
/// <see cref="DependencySourceObservationKind.Read"/>. A caller that hashed the same file
/// before indexing can compare the two digests to learn whether it was rewritten in between;
/// where there is no digest there was no second observation to compare against.
/// </summary>
public readonly record struct DependencySourceObservation(
	DependencySourceObservationKind Kind,
	string? ContentDigest = null);

public sealed record DependencyIndexSnapshot(
	string SourceRoot,
	string ManifestGeneration,
	string DeclarationRevision,
	IReadOnlyList<FileFacts> Files,
	IReadOnlyList<DeclarationFact> Declarations,
	IReadOnlyList<DependencyEdge> Edges,
	IReadOnlyDictionary<string, IReadOnlyList<DependencyEdge>> EdgesBySource,
	IReadOnlyDictionary<string, IReadOnlyList<DependencyEdge>> EdgesByTarget,
	DependencyFactsCoverage Coverage,
	DependencyIndexMetrics Metrics)
{
	public IReadOnlyDictionary<string, FileFacts> FileByPath { get; init; } =
		new Dictionary<string, FileFacts>(StringComparer.Ordinal);

	/// <summary>
	/// What this pass observed of each manifest file's content, keyed by absolute path. A
	/// snapshot served whole from the cache observed nothing and reports nothing.
	/// </summary>
	public IReadOnlyDictionary<string, DependencySourceObservation> ContentObservations { get; init; } =
		new Dictionary<string, DependencySourceObservation>(StringComparer.Ordinal);
}

public sealed record RelatedFile(
	string Path,
	ResolutionStatus Status,
	IReadOnlyList<string> Reasons,
	IReadOnlyList<string> Candidates,
	bool CrossScope,
	long EstimatedTokens);

public sealed record SeedRelatedFiles(
	string Seed,
	LanguageId LanguageId,
	IReadOnlyList<RelatedFile> Dependencies,
	IReadOnlyList<RelatedFile> Dependents,
	string? NoFactsReason);

public sealed record DependencyRelatedResult(
	DependencyIndexSnapshot Index,
	IReadOnlyList<SeedRelatedFiles> Seeds);

public readonly record struct DependencyIndexProgress(int CompletedFiles, int TotalFiles);
