using System.Runtime.CompilerServices;

namespace DevProjex.Infrastructure.FileSystem;

public sealed partial class FileSystemScanner
{
    private sealed class LocalExtensionScanState
    {
        public HashSet<string> Extensions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public MutableIgnoreOptionCounts Counts;
    }

    private sealed class IgnoreSectionSnapshotLocalState
    {
        public HashSet<string> Extensions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public MutableIgnoreOptionCounts RawCounts;
        public IgnoreOptionCounts EffectiveCounts { get; set; } = IgnoreOptionCounts.Empty;
        public IgnoreControllerImpactCounts ControllerImpactCounts { get; set; } = IgnoreControllerImpactCounts.Empty;
    }

    private sealed record RootSelectionScanPlan(
        List<string> SelectedRootPaths,
        List<FileSystemDirectoryEntry> DirectoryToggleCandidates,
        List<DirectoryScanFacts> ControllerImpactCandidates,
        bool RootAccessDenied,
        bool HadAccessDenied);

    private sealed class RootDirectoryToggleCandidateAccumulator
    {
        public int HiddenFolders { get; set; }
        public int DotFolders { get; set; }
        public bool IsEmpty => HiddenFolders == 0 && DotFolders == 0;
    }

    private struct DirectoryScanNode(string path, string relativePath, int parentIndex, bool isAccessDenied)
    {
        public string Path { get; } = path;
        public string RelativePath { get; } = relativePath;
        public int ParentIndex { get; } = parentIndex;
        public bool IsAccessDenied { get; set; } = isAccessDenied;
    }

    private readonly record struct DirectoryToggleRuleState(
        bool CanTraverseChildren,
        bool IsSelfIgnoredButTraversed);

    private readonly record struct DirectoryScanFacts(
        string Name,
        string FullPath,
        string RelativePath,
        bool IsHidden,
        bool IsDot,
        bool IsSmartIgnored,
        bool IsSmartIgnoredCandidate,
        IgnoreRules.GitIgnoreEvaluation GitIgnoreEvaluation,
        IgnoreRules.GitIgnoreEvaluation GitIgnoreCandidateEvaluation);

    private readonly record struct FileScanFacts(
        string Name,
        string RelativePath,
        string Extension,
        bool IsHidden,
        bool IsDot,
        bool IsEmpty,
        bool IsExtensionless,
        bool IsSmartIgnored,
        bool IsSmartIgnoredCandidate,
        bool IsGitIgnored,
        bool IsGitIgnoredCandidate);

    private readonly record struct EffectiveFileVisibilityProfile(
        bool BaseVisible,
        bool HiddenFilesVisible,
        bool DotFilesVisible,
        bool EmptyFilesVisible,
        bool ExtensionlessFilesVisible,
        bool ControllerBaselineVisible,
        bool GitIgnoreVisible,
        bool SmartIgnoreVisible);

    private struct EffectiveIgnoreScanNode(
        string path,
        string relativePath,
        string name,
        int parentIndex,
        bool isAccessDenied,
        bool isHidden,
        bool isDot,
        IgnoreControllerImpactCounts directControllerImpactCounts,
        DirectoryToggleRuleState extensionDiscoveryRuleState,
        DirectoryToggleRuleState baseRuleState,
        DirectoryToggleRuleState hiddenFoldersRuleState,
        DirectoryToggleRuleState dotFoldersRuleState)
    {
        public string Path { get; } = path;
        public string RelativePath { get; } = relativePath;
        public string Name { get; } = name;
        public int ParentIndex { get; } = parentIndex;
        public bool IsAccessDenied { get; set; } = isAccessDenied;
        public bool IsHidden { get; } = isHidden;
        public bool IsDot { get; } = isDot;
        public IgnoreControllerImpactCounts DirectControllerImpactCounts { get; } = directControllerImpactCounts;
        public DirectoryToggleRuleState ExtensionDiscoveryRuleState { get; } = extensionDiscoveryRuleState;
        public DirectoryToggleRuleState BaseRuleState { get; } = baseRuleState;
        public DirectoryToggleRuleState HiddenFoldersRuleState { get; } = hiddenFoldersRuleState;
        public DirectoryToggleRuleState DotFoldersRuleState { get; } = dotFoldersRuleState;

        public bool CanAnyVariantTraverseChildren =>
            ExtensionDiscoveryRuleState.CanTraverseChildren ||
            BaseRuleState.CanTraverseChildren ||
            HiddenFoldersRuleState.CanTraverseChildren ||
            DotFoldersRuleState.CanTraverseChildren;
    }

    private struct EffectiveIgnoreNodeFileMetrics
    {
        public int ExtensionDiscoveryVisibleFiles;
        public int BaseVisibleFiles;
        public int HiddenFilesVisibleFiles;
        public int DotFilesVisibleFiles;
        public int EmptyFilesVisibleFiles;
        public int ExtensionlessFilesVisibleFiles;
        public int ControllerBaselineVisibleFiles;
        public int GitIgnoreVisibleFiles;
        public int SmartIgnoreVisibleFiles;
        public int HiddenFilesAppearWhenToggled;
        public int HiddenFilesDisappearWhenToggled;
        public int DotFilesAppearWhenToggled;
        public int DotFilesDisappearWhenToggled;
        public int EmptyFilesAppearWhenToggled;
        public int EmptyFilesDisappearWhenToggled;
        public int ExtensionlessFilesAppearWhenToggled;
        public int ExtensionlessFilesDisappearWhenToggled;
    }

    private struct EffectiveIgnoreNodeVisibilityState
    {
        public bool IsAccessDenied;
        public int RawDiscoveryVisibleChildren;
        public bool ExtensionDiscoveryFinalVisible;
        public int BaseVisibleChildren;
        public int HiddenFoldersVisibleChildren;
        public int DotFoldersVisibleChildren;
        public int HiddenFilesVisibleChildren;
        public int DotFilesVisibleChildren;
        public int EmptyFilesVisibleChildren;
        public int ExtensionlessFilesVisibleChildren;
        public int EmptyFoldersVisibleChildren;
        public int ControllerBaselineVisibleChildren;
        public int GitIgnoreVisibleChildren;
        public int SmartIgnoreVisibleChildren;
        public bool BaseLocalVisible;
        public bool HiddenFoldersLocalVisible;
        public bool DotFoldersLocalVisible;
        public bool HiddenFilesLocalVisible;
        public bool DotFilesLocalVisible;
        public bool EmptyFilesLocalVisible;
        public bool ExtensionlessFilesLocalVisible;
        public bool EmptyFoldersLocalVisible;
        public bool ControllerBaselineLocalVisible;
        public bool GitIgnoreLocalVisible;
        public bool SmartIgnoreLocalVisible;
        public bool BaseFinalVisible;
        public bool HiddenFoldersFinalVisible;
        public bool DotFoldersFinalVisible;
        public bool HiddenFilesFinalVisible;
        public bool DotFilesFinalVisible;
        public bool EmptyFilesFinalVisible;
        public bool ExtensionlessFilesFinalVisible;
        public bool EmptyFoldersFinalVisible;
        public bool ControllerBaselineFinalVisible;
        public bool GitIgnoreFinalVisible;
        public bool SmartIgnoreFinalVisible;
    }

    private struct MutableIgnoreOptionCounts
    {
        public int HiddenFolders;
        public int HiddenFiles;
        public int DotFolders;
        public int DotFiles;
        public int EmptyFolders;
        public int EmptyFiles;
        public int ExtensionlessFiles;

        public readonly bool IsEmpty =>
            HiddenFolders == 0 &&
            HiddenFiles == 0 &&
            DotFolders == 0 &&
            DotFiles == 0 &&
            EmptyFolders == 0 &&
            EmptyFiles == 0 &&
            ExtensionlessFiles == 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(in MutableIgnoreOptionCounts other)
        {
            HiddenFolders += other.HiddenFolders;
            HiddenFiles += other.HiddenFiles;
            DotFolders += other.DotFolders;
            DotFiles += other.DotFiles;
            EmptyFolders += other.EmptyFolders;
            EmptyFiles += other.EmptyFiles;
            ExtensionlessFiles += other.ExtensionlessFiles;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(in IgnoreOptionCounts other)
        {
            HiddenFolders += other.HiddenFolders;
            HiddenFiles += other.HiddenFiles;
            DotFolders += other.DotFolders;
            DotFiles += other.DotFiles;
            EmptyFolders += other.EmptyFolders;
            EmptyFiles += other.EmptyFiles;
            ExtensionlessFiles += other.ExtensionlessFiles;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly IgnoreOptionCounts ToImmutable()
        {
            return new IgnoreOptionCounts(HiddenFolders, HiddenFiles, DotFolders, DotFiles, EmptyFolders, ExtensionlessFiles, EmptyFiles);
        }
    }
}
