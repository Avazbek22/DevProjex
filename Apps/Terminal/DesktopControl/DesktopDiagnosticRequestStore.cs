using System.Text.Json;

namespace DevProjex.Terminal.DesktopControl;

public sealed record DesktopDiagnosticRequest(
	string ProjectPath,
	string OutputPath,
	string Scenario);

public static class DesktopDiagnosticRequestStore
{
	public const string EnvironmentVariable = "DEVPROJEX_INTERNAL_DESKTOP_DIAGNOSTIC";

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	public static bool HasPendingRequest =>
		!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable));

	public static string Create(DesktopDiagnosticRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		var directory = new DesktopControlPaths().DiagnosticDirectory;
		DesktopInstanceRegistry.EnsurePrivateDirectory(directory);
		var path = Path.Combine(directory, $"{Guid.NewGuid():N}.json");
		File.WriteAllText(
			path,
			JsonSerializer.Serialize(request, JsonOptions),
			new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		DesktopInstanceRegistry.SetPrivateFileMode(path);
		return path;
	}

	public static DesktopDiagnosticRequest? TryConsume()
	{
		var path = Environment.GetEnvironmentVariable(EnvironmentVariable);
		string? safeRequestPath = null;
		Environment.SetEnvironmentVariable(EnvironmentVariable, null);
		if (string.IsNullOrWhiteSpace(path))
			return null;

		try
		{
			var fullPath = Path.GetFullPath(path);
			var expectedDirectory = Path.GetFullPath(
				new DesktopControlPaths().DiagnosticDirectory);
			if (!PathUtility.IsPathInside(fullPath, expectedDirectory))
				return null;
			safeRequestPath = fullPath;
			return DesktopRequestEnvelopeReader.Read<DesktopDiagnosticRequest>(
				fullPath,
				JsonOptions);
		}
		catch
		{
			return null;
		}
		finally
		{
			Delete(safeRequestPath);
		}
	}

	public static void Delete(string? path)
	{
		if (!string.IsNullOrWhiteSpace(path))
			DesktopInstanceRegistry.TryDelete(path);
	}
}
