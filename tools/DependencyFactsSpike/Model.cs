using System.Text.Json.Serialization;

namespace DependencyFactsSpike;

internal enum LanguageId { CSharp, TypeScript, JavaScript, Tsx, Python }
internal enum SymbolKind { Class, Struct, Interface, Record, Enum, Delegate, Function, Module }
internal enum EvidenceLayer { ExplicitImport, TypeReference }
internal enum ResolutionStatus { Resolved, Ambiguous, External, Unresolved }

internal sealed record SourceSite(string File, int Line, string Evidence);

internal sealed record SymbolIdentity(
	string ScopeId,
	LanguageId Language,
	SymbolKind Kind,
	string QualifiedName,
	int GenericArity);

internal sealed record DeclarationFact(SymbolIdentity Identity, IReadOnlyList<SourceSite> Sites);

internal sealed record ImportContext(
	string Specifier,
	string? ImportedName,
	string? Alias,
	bool IsWildcard,
	int RelativeLevel,
	int Line,
	string Evidence);

internal sealed record ReferenceFact(
	EvidenceLayer Layer,
	string Name,
	int GenericArity,
	int Line,
	string Evidence,
	string SyntaxKind);

internal sealed record FileFacts(
	string Path,
	string ScopeId,
	LanguageId Language,
	string ContentHash,
	bool HasSyntaxErrors,
	IReadOnlyList<DeclarationFact> Declarations,
	IReadOnlyList<ImportContext> Imports,
	IReadOnlyList<ReferenceFact> References,
	IReadOnlyList<string> ContextNamespaces,
	IReadOnlyDictionary<string, string> Aliases,
	IReadOnlyList<string> TypeParameters);

internal sealed record DependencyEdge(
	string Source,
	string? Target,
	EvidenceLayer Layer,
	ResolutionStatus Status,
	string Reference,
	int Line,
	string Evidence,
	string Reason,
	IReadOnlyList<string> Candidates);

internal sealed record RepositoryMetrics(
	int FilesParsed,
	int ExtractionErrors,
	int LayerAReferences,
	int LayerBReferences,
	IReadOnlyDictionary<ResolutionStatus, int> StatusCounts,
	long ElapsedMilliseconds,
	long PeakWorkingSetBytes,
	int ParsedFiles,
	int ReusedFiles,
	int ReresolvedFiles);

internal sealed record RepositoryResult(
	string Root,
	string CorpusSha,
	IReadOnlyList<FileFacts> Files,
	IReadOnlyList<DeclarationFact> Symbols,
	IReadOnlyList<DependencyEdge> Edges,
	RepositoryMetrics Metrics,
	string ResultSha256);

internal sealed record IndexOptions(
	string Root,
	string Output,
	string CorpusSha,
	bool ReverseFileOrder,
	string GrammarCache,
	string? PreviousFacts = null);

internal sealed record FixtureExpectation(
	string Name,
	IReadOnlyList<ExpectedEdge> Edges,
	IReadOnlyList<AbsentEdge>? AbsentEdges = null,
	int? ParsedAfterSourceChange = null,
	int? ParsedAfterConfigChange = null,
	int? ReresolvedAfterConfigChange = null);

internal sealed record ExpectedEdge(
	string Source,
	string Reference,
	ResolutionStatus Status,
	string? Target = null,
	string? ReasonContains = null);

internal sealed record AbsentEdge(string Source, string Reference);

[JsonSourceGenerationOptions(
	WriteIndented = true,
	PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
	UseStringEnumConverter = true)]
[JsonSerializable(typeof(RepositoryResult))]
[JsonSerializable(typeof(RepositoryMetrics))]
[JsonSerializable(typeof(IReadOnlyList<FileFacts>))]
[JsonSerializable(typeof(FixtureExpectation))]
internal partial class SpikeJsonContext : JsonSerializerContext;
