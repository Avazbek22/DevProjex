namespace DevProjex.Application.Services;

public enum McpConnectionStatus
{
	Connected,
	Updated,
	ManualConfiguration,
	ClientNotFound,
	InvalidConfiguration,
	ProcessFailed,
	TimedOut
}

public sealed record McpConnectionRequest(
	McpConnectionClient Client,
	McpConnectionMode Mode,
	string ExecutablePath,
	string ProjectRoot);

public sealed record McpConnectionResult(
	McpConnectionStatus Status,
	string UserMessage,
	string? NextCommand = null,
	string? ManualConfiguration = null,
	IReadOnlyList<string>? SuggestedConfigPaths = null,
	string? CommandOutput = null,
	string? TargetPath = null,
	bool Replaced = false)
{
	public bool Succeeded => Status is McpConnectionStatus.Connected or McpConnectionStatus.Updated;

	public bool RequiresManualConfiguration => Status is
		McpConnectionStatus.ManualConfiguration or
		McpConnectionStatus.ClientNotFound or
		McpConnectionStatus.InvalidConfiguration or
		McpConnectionStatus.ProcessFailed or
		McpConnectionStatus.TimedOut;
}

public interface IMcpConnectionService
{
	Task<McpConnectionResult> ConnectAsync(
		McpConnectionRequest request,
		CancellationToken cancellationToken = default);

	string CreatePrintableConfiguration(McpConnectionRequest request);
}
