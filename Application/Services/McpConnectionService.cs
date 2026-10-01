namespace DevProjex.Application.Services;

public enum McpConnectionStatus
{
	Connected,
	Updated,
	ManualConfiguration,
	ClientNotFound,
	InvalidConfiguration,
	ProcessFailed,
	TimedOut,
	Canceled,
	ProjectNotFound
}

public enum McpConnectionProjectRootState
{
	Directory,
	Missing,
	NotDirectory
}

/// <summary>
/// Every surface that registers or prints a connection checks the project root here first, so a
/// client is never pointed at a path that cannot be served and all surfaces word the refusal alike.
/// </summary>
public static class McpConnectionProjectRoot
{
	public const string ErrorCode = "DPX-PROJECT-NOT-FOUND";

	public static McpConnectionProjectRootState Inspect(string projectRoot)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		if (System.IO.Directory.Exists(projectRoot))
			return McpConnectionProjectRootState.Directory;
		return File.Exists(projectRoot)
			? McpConnectionProjectRootState.NotDirectory
			: McpConnectionProjectRootState.Missing;
	}

	public static string? DescribeProblem(LocalizationService localization, string projectRoot)
	{
		ArgumentNullException.ThrowIfNull(localization);
		return Inspect(projectRoot) switch
		{
			McpConnectionProjectRootState.Missing => localization["Mcp.Connect.ProjectMissing"],
			McpConnectionProjectRootState.NotDirectory => localization["Mcp.Connect.ProjectNotDirectory"],
			_ => null
		};
	}

	/// <summary>
	/// The refusal shown by surfaces that present a connection result: the problem on the first line and
	/// the path it concerns on the second.
	/// </summary>
	public static McpConnectionResult? CreateRefusal(LocalizationService localization, string projectRoot) =>
		DescribeProblem(localization, projectRoot) is { } problem
			? new McpConnectionResult(McpConnectionStatus.ProjectNotFound, problem + "\n" + projectRoot)
			: null;
}

public sealed record McpConnectionRequest(
	McpConnectionClient Client,
	McpConnectionMode Mode,
	string ExecutablePath,
	string ProjectRoot,
	bool ReplaceExistingFields = false,
	string? ExpectedExistingEntryFingerprint = null);

// NextCommand names a client command to run in the project folder; NextStep is a complete
// localized instruction that is shown as is.
public sealed record McpConnectionResult(
	McpConnectionStatus Status,
	string UserMessage,
	string? NextCommand = null,
	string? ManualConfiguration = null,
	IReadOnlyList<string>? SuggestedConfigPaths = null,
	string? CommandOutput = null,
	string? TargetPath = null,
	bool Replaced = false,
	IReadOnlyList<string>? FieldsToReplace = null,
	string? ExistingEntryFingerprint = null,
	string? NextStep = null)
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
