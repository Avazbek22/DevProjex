namespace DevProjex.Kernel.Models;

public sealed record ProjectSelectionProfile(
	IReadOnlyCollection<string> SelectedRootFolders,
	IReadOnlyCollection<string> SelectedExtensions,
	IReadOnlyCollection<IgnoreOptionId> SelectedIgnoreOptions);
