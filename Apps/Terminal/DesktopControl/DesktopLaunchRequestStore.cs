using System.Text.Json;
using DevProjex.Terminal.CommandLine;

namespace DevProjex.Terminal.DesktopControl;

public static class DesktopLaunchRequestStore
{
	public const string InternalRequestArgument = "--dpx-internal-desktop-request-file";

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	public static bool HasPendingRequest =>
		!string.IsNullOrWhiteSpace(
			Environment.GetEnvironmentVariable(InvocationEnvironment.DesktopRequestVariable));

	public static string[] PromoteInternalInvocation(string[] arguments)
	{
		if (arguments.Length != 2 ||
		    !string.Equals(
			    arguments[0],
			    InternalRequestArgument,
			    StringComparison.Ordinal))
		{
			return arguments;
		}

		Environment.SetEnvironmentVariable(
			InvocationEnvironment.DesktopRequestVariable,
			arguments[1]);
		Environment.SetEnvironmentVariable(
			InvocationEnvironment.TerminalHostVariable,
			null);
		return [];
	}

	public static async Task<string> CreateAsync(
		DesktopOpenRequest request,
		CancellationToken cancellationToken = default)
	{
		var directory = Path.Combine(Path.GetTempPath(), "DevProjex", "desktop-requests");
		DesktopInstanceRegistry.EnsurePrivateDirectory(directory);
		var path = Path.Combine(directory, $"{Guid.NewGuid():N}.json");
		var json = JsonSerializer.Serialize(request, JsonOptions);
		await File.WriteAllTextAsync(
			path,
			json,
			new UTF8Encoding(false),
			cancellationToken).ConfigureAwait(false);
		DesktopInstanceRegistry.SetPrivateFileMode(path);
		return path;
	}

	public static async Task<DesktopOpenRequest?> TryConsumeFromEnvironmentAsync(
		CancellationToken cancellationToken = default)
	{
		var path = Environment.GetEnvironmentVariable(InvocationEnvironment.DesktopRequestVariable);
		string? safeRequestPath = null;
		Environment.SetEnvironmentVariable(InvocationEnvironment.DesktopRequestVariable, null);
		if (string.IsNullOrWhiteSpace(path))
			return null;

		try
		{
			var fullPath = Path.GetFullPath(path);
			var expectedDirectory = Path.GetFullPath(
				Path.Combine(Path.GetTempPath(), "DevProjex", "desktop-requests"));
			if (!PathUtility.IsPathInside(fullPath, expectedDirectory))
				return null;
			safeRequestPath = fullPath;
			return await DesktopRequestEnvelopeReader
				.ReadAsync<DesktopOpenRequest>(fullPath, JsonOptions, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
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
}
