namespace DevProjex.Kernel.Models;

/// <summary>
/// Result of a clone operation.
/// </summary>
public sealed record GitCloneResult(
    bool Success,
    string LocalPath,
    ProjectSourceType SourceType,
    string? DefaultBranch,
    string? RepositoryName,
    string? ErrorMessage);
