namespace DevProjex.Avalonia.Views;

public sealed class McpConnectionRequestedEventArgs(
    McpConnectionClient client) : EventArgs
{
    public McpConnectionClient Client { get; } = client;
}
