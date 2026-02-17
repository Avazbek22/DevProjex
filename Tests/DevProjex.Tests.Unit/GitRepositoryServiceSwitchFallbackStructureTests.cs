namespace DevProjex.Tests.Unit;

public sealed class GitRepositoryServiceSwitchFallbackStructureTests
{
    [Fact]
    public void SwitchBranchAsync_ContainsResilientFallbackCommands()
    {
        var repoRoot = FindRepositoryRoot();
        var file = Path.Combine(repoRoot, "Infrastructure", "Git", "GitRepositoryService.cs");
        var content = File.ReadAllText(file);

        Assert.Contains("refs/heads/*:refs/remotes/origin/*", content, StringComparison.Ordinal);
        Assert.Contains("checkout -B", content, StringComparison.Ordinal);
        Assert.Contains("origin/{branchName}", content, StringComparison.Ordinal);
        Assert.Contains("checkout \\\"{branchName}\\\"", content, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) ||
                File.Exists(Path.Combine(dir, "DevProjex.sln")))
                return dir;

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
