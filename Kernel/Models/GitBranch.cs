namespace DevProjex.Kernel.Models;

/// <summary>
/// Represents a Git branch.
/// </summary>
public sealed record GitBranch(
    string Name,
    bool IsActive,
    bool IsRemote);
