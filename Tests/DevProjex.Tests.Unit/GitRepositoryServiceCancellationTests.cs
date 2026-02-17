using DevProjex.Infrastructure.Git;

namespace DevProjex.Tests.Unit;

public sealed class GitRepositoryServiceCancellationTests
{
    [Fact]
    public async Task IsGitAvailableAsync_PreCanceledToken_ThrowsWhenGitCliIsAvailable()
    {
        var service = new GitRepositoryService();

        if (!await service.IsGitAvailableAsync())
            return;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await service.IsGitAvailableAsync(cts.Token));
    }
}
