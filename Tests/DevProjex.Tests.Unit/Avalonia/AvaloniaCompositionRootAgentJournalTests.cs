using DevProjex.Avalonia.Services;
using DevProjex.Infrastructure.AgentJournal;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class AvaloniaCompositionRootAgentJournalTests
{
    [Fact]
    public async Task UnavailableJournalDirectoryDoesNotPreventServiceComposition()
    {
        using var temporary = new TemporaryDirectory();
        var blockedPath = Path.Combine(temporary.Path, "agent-journal");
        await File.WriteAllTextAsync(blockedPath, "keep", TestContext.Current.CancellationToken);

        var services = AvaloniaCompositionRoot.CreateDefault(
            DesktopStartupOptions.Default,
            () => temporary.Path);

        Assert.IsType<UnavailableAgentJournalReader>(services.AgentJournalReader);
        await Assert.ThrowsAsync<IOException>(async () =>
            await services.AgentJournalReader.ListSessionsAsync(
                cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<IOException>(async () =>
            await services.AgentJournalReader.ReadCallsAsync(
                "session", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<IOException>(async () =>
            await services.AgentJournalReader.ReadReceiptAsync(
                "session", TestContext.Current.CancellationToken));
        Assert.Throws<IOException>(() =>
        {
            _ = services.AgentJournalReader.WatchChangesAsync(
                "session", TestContext.Current.CancellationToken);
        });
        await Assert.ThrowsAsync<IOException>(async () =>
            await services.AgentJournalReader.ClearAsync(
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("keep", await File.ReadAllTextAsync(blockedPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AvailableJournalDirectoryUsesTheNormalStore()
    {
        using var temporary = new TemporaryDirectory();

        var services = AvaloniaCompositionRoot.CreateDefault(
            DesktopStartupOptions.Default,
            () => temporary.Path);

        Assert.IsType<AgentJournalStore>(services.AgentJournalReader);
    }
}
