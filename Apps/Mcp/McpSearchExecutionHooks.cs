namespace DevProjex.Mcp;

internal static class McpSearchExecutionHooks
{
	private static Action<string>? afterScan;

	internal static Action<string>? AfterScan
	{
		get => Volatile.Read(ref afterScan);
		set => Volatile.Write(ref afterScan, value);
	}
}
