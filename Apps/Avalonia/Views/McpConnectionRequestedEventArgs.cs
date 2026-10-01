namespace DevProjex.Avalonia.Views;

public sealed class McpConnectionRequestedEventArgs(
    McpConnectionClient client,
    McpConnectionMode mode) : EventArgs
{
    public McpConnectionClient Client { get; } = client;
    public McpConnectionMode Mode { get; } = mode;
}
