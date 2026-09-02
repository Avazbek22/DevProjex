using System.Text.Json;
using DevProjex.Terminal.CommandLine;

namespace DevProjex.Terminal.DesktopControl;

public static class DesktopLaunchRequestStore
{
	public const string InternalRequestArgument = "--dpx-internal-desktop-request-file";
	private static readonly TimeSpan AbandonedRequestAge = TimeSpan.FromHours(24);

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

	public static Task<string> CreateAsync(
		DesktopOpenRequest request,
		CancellationToken cancellationToken = default) =>
		CreateAsync(request, DesktopInstanceRegistry.SetPrivateFileMode, cancellationToken);

	internal static async Task<string> CreateAsync(
		DesktopOpenRequest request,
		Action<string> protectFile,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(protectFile);
		cancellationToken.ThrowIfCancellationRequested();
		var directory = new DesktopControlPaths().RequestDirectory;
		DesktopInstanceRegistry.EnsurePrivateDirectory(directory);
		RemoveAbandonedRequests(directory, cancellationToken);
		var path = Path.Combine(directory, $"{Guid.NewGuid():N}.json");
		try
		{
			var json = JsonSerializer.Serialize(request, JsonOptions);
			await File.WriteAllTextAsync(
				path,
				json,
				new UTF8Encoding(false),
				cancellationToken).ConfigureAwait(false);
			protectFile(path);
			return path;
		}
		catch
		{
			DesktopInstanceRegistry.TryDelete(path);
			throw;
		}
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
				new DesktopControlPaths().RequestDirectory);
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

	private static void RemoveAbandonedRequests(
		string directory,
		CancellationToken cancellationToken)
	{
		var cutoff = DateTime.UtcNow - AbandonedRequestAge;
		var activeRequestPath = ResolveActiveRequestPath();
		try
		{
			foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
			{
				cancellationToken.ThrowIfCancellationRequested();
				try
				{
					if (!IsOwnedRequestFile(path) ||
					    activeRequestPath is not null && PathComparer.Default.Equals(path, activeRequestPath))
					{
						continue;
					}

					var attributes = File.GetAttributes(path);
					if (attributes.HasFlag(FileAttributes.ReparsePoint) ||
					    File.GetLastWriteTimeUtc(path) > cutoff)
					{
						continue;
					}

					DesktopInstanceRegistry.TryDelete(path);
				}
				catch (Exception exception) when (exception is
				       IOException or
				       UnauthorizedAccessException or
				       NotSupportedException)
				{
				}
			}
		}
		catch (Exception exception) when (exception is
		       IOException or
		       UnauthorizedAccessException or
		       System.Security.SecurityException)
		{
			// Abandoned-request cleanup must not prevent a new desktop launch.
		}
	}

	private static bool IsOwnedRequestFile(string path) =>
		string.Equals(Path.GetExtension(path), ".json", StringComparison.Ordinal) &&
		Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _);

	private static string? ResolveActiveRequestPath()
	{
		var path = Environment.GetEnvironmentVariable(InvocationEnvironment.DesktopRequestVariable);
		if (string.IsNullOrWhiteSpace(path))
			return null;
		try
		{
			return Path.GetFullPath(path);
		}
		catch (Exception exception) when (exception is
		       ArgumentException or
		       NotSupportedException or
		       PathTooLongException)
		{
			return null;
		}
	}
}
