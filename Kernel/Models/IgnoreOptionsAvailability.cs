namespace DevProjex.Kernel.Models;

public sealed record IgnoreOptionsAvailability(
	bool IncludeGitIgnore,
	bool IncludeSmartIgnore,
	bool IncludeHiddenFolders = true,
	int HiddenFoldersCount = 0,
	bool IncludeHiddenFiles = true,
	int HiddenFilesCount = 0,
	bool IncludeDotFolders = true,
	int DotFoldersCount = 0,
	bool IncludeDotFiles = true,
	int DotFilesCount = 0,
	bool IncludeEmptyFolders = false,
	int EmptyFoldersCount = 0,
	bool IncludeExtensionlessFiles = false,
	int ExtensionlessFilesCount = 0,
	bool IncludeEmptyFiles = false,
	int EmptyFilesCount = 0,
	bool ShowAdvancedCounts = false);
