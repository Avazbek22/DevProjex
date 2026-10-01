using System.Text.Json;

namespace DevProjex.Terminal.DesktopControl;

public sealed record StoreScreenshotCaptureRequest(
	string ProjectPath,
	string SessionDirectory,
	string AppDataDirectory,
	string LanguageCode,
	IReadOnlyList<string>? LiveContextSelection = null);

public static class StoreScreenshotCaptureRequestStore
{
	public const string EnvironmentVariable = "DEVPROJEX_INTERNAL_STORE_CAPTURE";
	public const string SessionRootName = "store-screenshot-captures";
	internal const int MaximumLiveContextSelectionCount = 32;

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

			var request = DesktopRequestEnvelopeReader.Read<StoreScreenshotCaptureRequest>(
				fullRequestPath,
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

		if (!IsValidLiveContextSelection(request.LiveContextSelection, request.ProjectPath))
			return false;

		var sessionDirectory = Path.GetFullPath(request.SessionDirectory);
		var appDataDirectory = Path.GetFullPath(request.AppDataDirectory);
		return PathUtility.IsPathInside(sessionDirectory, sessionRoot) &&
		       PathUtility.IsPathInside(appDataDirectory, sessionDirectory);
	}

	private static bool IsValidLiveContextSelection(
		IReadOnlyList<string>? selection,
		string projectPath)
	{
		if (selection is null)
			return true;
		if (selection.Count is 0 or > MaximumLiveContextSelectionCount)
			return false;

		var projectRoot = Path.GetFullPath(projectPath);
		foreach (var relativePath in selection)
		{
			// Selection entries name project items by their relative path; anything that can
			// resolve outside the capture project is rejected together with the whole request.
			if (string.IsNullOrWhiteSpace(relativePath) ||
				Path.IsPathRooted(relativePath) ||
				relativePath.Split(['/', '\\']).Any(static segment => segment is "" or "." or ".."))
			{
				return false;
			}

			var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relativePath));
			if (!PathUtility.IsPathInside(fullPath, projectRoot) ||
				PathComparer.Default.Equals(fullPath, projectRoot))
			{
				return false;
			}
		}

		return true;
	}
}
