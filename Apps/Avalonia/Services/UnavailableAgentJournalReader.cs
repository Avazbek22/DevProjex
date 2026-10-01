namespace DevProjex.Avalonia.Services;

internal sealed class UnavailableAgentJournalReader : IAgentJournalReader
{
    public AgentJournalRetentionPolicy Retention => AgentJournalRetentionPolicy.Default;

    public ValueTask<IReadOnlyList<AgentJournalSession>> ListSessionsAsync(
        string? projectRoot = null,
        int limit = 200,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<IReadOnlyList<AgentJournalSession>>(StorageUnavailable());

    public ValueTask<IReadOnlyList<AgentJournalCall>> ReadCallsAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<IReadOnlyList<AgentJournalCall>>(StorageUnavailable());

    public ValueTask<AgentJournalReceipt?> ReadReceiptAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<AgentJournalReceipt?>(StorageUnavailable());

    public IAsyncEnumerable<AgentJournalChange> WatchChangesAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        throw StorageUnavailable();

    public ValueTask<int> ClearAsync(
        string? projectRoot = null,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<int>(StorageUnavailable());

    private static IOException StorageUnavailable() =>
        new("Agent journal storage is unavailable.");
}
