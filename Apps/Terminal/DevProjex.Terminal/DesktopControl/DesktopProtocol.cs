using System.Text.Json;

namespace DevProjex.Terminal.DesktopControl;

public static class DesktopProtocol
{
	public const int CurrentVersion = 1;
	public const int MaximumMessageBytes = 1024 * 1024;
}

public sealed record DesktopProtocolRequest(
	int ProtocolVersion,
	string RequestId,
	string? InstanceId,
	string Action,
	JsonElement Payload);

public sealed record DesktopProtocolError(string Code, string Message);

public sealed record DesktopProtocolResponse(
	int ProtocolVersion,
	string RequestId,
	bool Ok,
	IReadOnlyDictionary<string, object?>? State,
	DesktopProtocolError? Error);

public sealed record DesktopInstanceRegistration(
	int ProtocolVersion,
	string InstanceId,
	int ProcessId,
	long ProcessStartTimeUtcTicks,
	string? ProjectPath,
	DateTimeOffset LastActiveUtc,
	string Transport,
	string Endpoint);

public sealed record DesktopTarget(
	string? InstanceId = null,
	string? ProjectPath = null,
	TimeSpan? Timeout = null);

public sealed class DesktopControlException(
	string code,
	string message,
	int exitCode = CommandLineExitCodes.DesktopUnavailable,
	Exception? innerException = null)
	: Exception(message, innerException)
{
	public string Code { get; } = code;
	public int ExitCode { get; } = exitCode;
}
