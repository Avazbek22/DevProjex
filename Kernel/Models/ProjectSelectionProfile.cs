namespace DevProjex.Kernel.Models;

public sealed record ProjectSelectionProfile(
	IReadOnlyCollection<string> SelectedRootFolders,
	IReadOnlyCollection<string> SelectedExtensions,
	IReadOnlyCollection<IgnoreOptionId> SelectedIgnoreOptions,
	IReadOnlyDictionary<string, bool>? RootFolderStates = null,
	IReadOnlyDictionary<string, bool>? ExtensionStates = null,
	IReadOnlyDictionary<IgnoreOptionId, bool>? IgnoreOptionStates = null,
	IReadOnlyCollection<string>? SelectedPaths = null,
	IReadOnlyCollection<MarkedSecretProfileEntry>? MarkedSecrets = null);

public sealed record MarkedSecretProfileEntry(
	string H,
	string? Key,
	int Length,
	string? RelativePath = null,
	int? SourceOffset = null,
	ManualRedactionClass Class = ManualRedactionClass.Secret);
