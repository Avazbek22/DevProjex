namespace DevProjex.Mcp;

internal static class McpErrorCodes
{
	public const string RootViolation = "DPX-MCP-ROOT-VIOLATION";
	public const string UnknownProject = "DPX-MCP-UNKNOWN-PROJECT";
	public const string PackExpired = "DPX-MCP-PACK-EXPIRED";
	public const string PackTooLarge = "DPX-MCP-PACK-TOO-LARGE";
	public const string InvalidRange = "DPX-MCP-INVALID-RANGE";
	public const string InvalidPattern = "DPX-MCP-INVALID-PATTERN";
	public const string PayloadTruncated = "DPX-MCP-PAYLOAD-TRUNCATED";
	public const string PathNotFound = "DPX-MCP-PATH-NOT-FOUND";
	public const string InvalidArguments = "DPX-MCP-INVALID-ARGUMENTS";
	public const string ProjectUnavailable = "DPX-MCP-PROJECT-UNAVAILABLE";
	public const string RemoteDisabled = "DPX-MCP-REMOTE-DISABLED";
	public const string RemoteFailed = "DPX-MCP-REMOTE-FAILED";
}

internal sealed class McpToolException(string code, string message) : Exception(message)
{
	public string Code { get; } = code;
}
