namespace DevProjex.Application.Services;

public enum McpClientLaunchStatus
{
	Opened,
	ClientNotFound,
	Failed,
	UnsupportedClient
}

public sealed record McpClientLaunchRequest(
	McpConnectionClient Client,
	string ProjectRoot);

public sealed record McpClientLaunchResult(
	McpClientLaunchStatus Status,
	string? ErrorMessage = null,
	string? ManualCommand = null)
{
	public bool Succeeded => Status == McpClientLaunchStatus.Opened;
}

public interface IMcpClientLaunchService
{
	Task<McpClientLaunchResult> OpenAsync(
		McpClientLaunchRequest request,
		CancellationToken cancellationToken = default);
}
