namespace DevProjex.Kernel.Models;

public sealed record ProjectSelectionProfile(
	IReadOnlyCollection<string> SelectedRootFolders,
	IReadOnlyCollection<string> SelectedExtensions,
	IReadOnlyCollection<IgnoreOptionId> SelectedIgnoreOptions,
	IReadOnlyDictionary<string, bool>? RootFolderStates = null,
	IReadOnlyDictionary<string, bool>? ExtensionStates = null,
	IReadOnlyDictionary<IgnoreOptionId, bool>? IgnoreOptionStates = null,
	IReadOnlyCollection<string>? SelectedPaths = null);
