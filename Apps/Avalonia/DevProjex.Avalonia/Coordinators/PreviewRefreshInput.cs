using DevProjex.Avalonia.Services;
using DevProjex.Kernel;

namespace DevProjex.Avalonia.Coordinators;

internal sealed record PreviewRefreshInput(
    PreviewContentMode SelectedMode,
    IReadOnlySet<string> SelectedPaths,
    bool HasSelection,
    TreeTextFormat TreeFormat,
    string NoCheckedFilesText,
    string NoTextContentText,
    string NoDataText,
    string? CurrentPath,
    TreeNodeDescriptor? CurrentTreeRoot,
    IReadOnlyList<string>? CurrentTreeOrderedFilePaths,
    ExportPathPresentation? PathPresentation,
    PreviewCacheKeyData CacheKey);
