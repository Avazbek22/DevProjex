using System.Text.Json;
using DevProjex.Terminal.CommandLine;

namespace DevProjex.Terminal.DesktopControl;

public sealed record StoreScreenshotCaptureRequest(
	string ProjectPath,
	string SessionDirectory,
	string AppDataDirectory,
	string LanguageCode);

public static class StoreScreenshotCaptureRequestStore
{
	public const string EnvironmentVariable = "DEVPROJEX_INTERNAL_STORE_CAPTURE";
	public const string SessionRootName = "store-screenshot-captures";

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	public static bool HasPendingRequest =>
		!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable));

	public static StoreScreenshotCaptureRequest? TryConsume()
	{
		var requestPath = Environment.GetEnvironmentVariable(EnvironmentVariable);
		string? safeRequestPath = null;
		Environment.SetEnvironmentVariable(EnvironmentVariable, null);
		if (string.IsNullOrWhiteSpace(requestPath))
			return null;

		try
		{
			var sessionRoot = GetSessionRoot();
			var fullRequestPath = Path.GetFullPath(requestPath);
			if (!PathUtility.IsPathInside(fullRequestPath, sessionRoot))
				return null;
			safeRequestPath = fullRequestPath;

			var file = new FileInfo(fullRequestPath);
			if (!file.Exists || file.Length > DesktopProtocol.MaximumMessageBytes)
				return null;

			var request = JsonSerializer.Deserialize<StoreScreenshotCaptureRequest>(
				File.ReadAllText(fullRequestPath),
				JsonOptions);
			return IsValid(request, sessionRoot) ? request : null;
		}
		catch
		{
			return null;
		}
		finally
		{
			if (safeRequestPath is not null)
				DesktopInstanceRegistry.TryDelete(safeRequestPath);
		}
	}

	public static string GetSessionRoot() => Path.GetFullPath(
		Path.Combine(Path.GetTempPath(), "DevProjex", SessionRootName));

	private static bool IsValid(
		StoreScreenshotCaptureRequest? request,
		string sessionRoot)
	{
		if (request is null ||
			!Directory.Exists(request.ProjectPath) ||
			!AppLanguageUtility.TryParseCode(request.LanguageCode, out _))
		{
			return false;
		}

		var sessionDirectory = Path.GetFullPath(request.SessionDirectory);
		var appDataDirectory = Path.GetFullPath(request.AppDataDirectory);
		return PathUtility.IsPathInside(sessionDirectory, sessionRoot) &&
		       PathUtility.IsPathInside(appDataDirectory, sessionDirectory);
	}
}
