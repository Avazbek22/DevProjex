using System.Text.Json;
using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.DesktopControl;

namespace DevProjex.Terminal.Execution;

public sealed class DesktopCommandHandler(
	ITerminalEnvironment environment,
	DesktopControlClient? client = null,
	DesktopProcessLauncher? launcher = null,
	bool writeOutput = true)
{
	private readonly DesktopControlClient _client = client ?? new DesktopControlClient();
	private readonly DesktopProcessLauncher _launcher = launcher ?? new DesktopProcessLauncher();

	public async Task<int> OpenAsync(
		DesktopOpenRequest request,
		CancellationToken cancellationToken)
	{
		if (request.NewWindow)
		{
			var launched = await _launcher.LaunchAsync(request, cancellationToken).ConfigureAwait(false);
			try
			{
				await WaitForLaunchedInstanceAsync(
					launched.ProcessId,
					request,
					cancellationToken).ConfigureAwait(false);
			}
			catch
			{
				DesktopInstanceRegistry.TryDelete(launched.RequestPath);
				throw;
			}
			WriteOutput(request.ProjectPath is null ? "desktop" : Path.GetFullPath(request.ProjectPath));
			return CommandLineExitCodes.Success;
		}

		var instances = await _client.ListAsync(cancellationToken).ConfigureAwait(false);
		var matching = FindSuitableInstance(instances, request.ProjectPath);
		if (matching is null)
		{
			if (instances.Count > 1)
			{
				throw new DesktopControlException(
					"DPX-DESKTOP-AMBIGUOUS",
					"Multiple desktop instances are running and none uniquely matches the project.");
			}

			var launched = await _launcher.LaunchAsync(request, cancellationToken).ConfigureAwait(false);
			try
			{
				await WaitForLaunchedInstanceAsync(
					launched.ProcessId,
					request,
					cancellationToken).ConfigureAwait(false);
			}
			catch
			{
				DesktopInstanceRegistry.TryDelete(launched.RequestPath);
				throw;
			}
			WriteOutput(request.ProjectPath is null ? "desktop" : Path.GetFullPath(request.ProjectPath));
			return CommandLineExitCodes.Success;
		}

		var response = await _client.SendAsync(
			matching,
			"open",
			request,
			request.WaitForCompletion ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(10),
			cancellationToken).ConfigureAwait(false);
		EnsureSuccess(response);
		WriteOutput(matching.InstanceId);
		return CommandLineExitCodes.Success;
	}

	public async Task<int> ListAsync(bool json, CancellationToken cancellationToken)
	{
		var instances = await _client.ListAsync(cancellationToken).ConfigureAwait(false);
		if (json)
		{
			environment.Output.WriteLine(JsonSerializer.Serialize(
				new
				{
					schemaVersion = 1,
					instances
				},
				new JsonSerializerOptions
				{
					WriteIndented = true,
					PropertyNamingPolicy = JsonNamingPolicy.CamelCase
				}));
		}
		else
		{
			foreach (var instance in instances)
				environment.Output.WriteLine($"{instance.InstanceId}\t{instance.ProcessId}\t{instance.ProjectPath ?? "-"}");
		}

		return CommandLineExitCodes.Success;
	}

	public async Task<int> SendAsync(
		DesktopTarget target,
		string action,
		object? payload,
		CancellationToken cancellationToken)
	{
		var instance = await _client.ResolveTargetAsync(target, cancellationToken).ConfigureAwait(false);
		var response = await _client.SendAsync(
			instance,
			action,
			payload,
			target.Timeout ?? TimeSpan.FromSeconds(10),
			cancellationToken).ConfigureAwait(false);
		EnsureSuccess(response);
		if (response.State is not null)
		{
			environment.Output.WriteLine(JsonSerializer.Serialize(
				response.State,
				new JsonSerializerOptions
				{
					WriteIndented = true,
					PropertyNamingPolicy = JsonNamingPolicy.CamelCase
				}));
		}
		else
		{
			environment.Output.WriteLine(instance.InstanceId);
		}

		return CommandLineExitCodes.Success;
	}

	private async Task WaitForLaunchedInstanceAsync(
		int processId,
		DesktopOpenRequest request,
		CancellationToken cancellationToken)
	{
		var deadline = DateTimeOffset.UtcNow + (
			request.WaitForCompletion
				? TimeSpan.FromMinutes(2)
				: TimeSpan.FromSeconds(10));
		while (DateTimeOffset.UtcNow < deadline)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var instances = await _client.ListAsync(cancellationToken).ConfigureAwait(false);
			var instance = instances.SingleOrDefault(candidate => candidate.ProcessId == processId);
			if (instance is not null)
			{
				var response = await _client.SendAsync(
					instance,
					"status",
					new { },
					TimeSpan.FromSeconds(10),
					cancellationToken).ConfigureAwait(false);
				EnsureSuccess(response);
				if (!request.WaitForCompletion)
					return;
				if (DesktopOpenReadiness.TryGetFailureCode(response.State, out var failureCode))
				{
					throw new DesktopControlException(
						failureCode,
						"DevProjex Desktop could not apply the startup request.");
				}
				if (DesktopOpenReadiness.IsApplied(request, response.State))
					return;
			}

			await Task.Delay(100, cancellationToken).ConfigureAwait(false);
		}

		throw new DesktopControlException(
			"DPX-DESKTOP-TIMEOUT",
			"DevProjex Desktop did not become ready before the timeout.");
	}

	private void WriteOutput(string value)
	{
		if (writeOutput)
			environment.Output.WriteLine(value);
	}

	private static DesktopInstanceRegistration? FindSuitableInstance(
		IReadOnlyList<DesktopInstanceRegistration> instances,
		string? projectPath)
	{
		if (!string.IsNullOrWhiteSpace(projectPath))
		{
			var normalizedProject = PathUtility.Normalize(projectPath);
			var matches = instances.Where(instance =>
				instance.ProjectPath is not null &&
				PathComparer.Default.Equals(instance.ProjectPath, normalizedProject)).ToArray();
			if (matches.Length == 1)
				return matches[0];
			if (matches.Length > 1)
			{
				throw new DesktopControlException(
					"DPX-DESKTOP-AMBIGUOUS",
					"More than one desktop instance has the requested project open.");
			}
		}

		return instances.Count == 1 ? instances[0] : null;
	}

	private static void EnsureSuccess(DesktopProtocolResponse response)
	{
		if (response.Ok)
			return;

		throw new DesktopControlException(
			response.Error?.Code ?? "DPX-DESKTOP-REQUEST-FAILED",
			response.Error?.Message ?? "The desktop request failed.");
	}
}

internal static class DesktopOpenReadiness
{
	public static bool IsApplied(
		DesktopOpenRequest request,
		IReadOnlyDictionary<string, object?>? state)
	{
		if (!ReadBoolean(state, "startupReady"))
			return false;

		if (request.ProjectPath is { Length: > 0 } projectPath)
		{
			var expectedProject = PathUtility.Normalize(projectPath);
			if (!ReadBoolean(state, "projectLoaded") ||
			    ReadPath(state, "projectPath") is not { } loadedProject ||
			    !PathComparer.Default.Equals(loadedProject, expectedProject))
			{
				return false;
			}
		}
		else if (request.UseLastProject && !ReadBoolean(state, "projectLoaded"))
		{
			return false;
		}

		if (request.OpenPreview &&
		    (!ReadBoolean(state, "previewOpen") ||
		     !StringEquals(state, "previewView", ToToken(request.PreviewView))))
		{
			return false;
		}

		if (request.TreeFormat is { } treeFormat &&
		    !StringEquals(state, "treeFormat", ToToken(treeFormat)))
		{
			return false;
		}

		if (request.Filter is not null && !StringEquals(state, "filter", request.Filter))
			return false;
		if (request.Search is not null && !StringEquals(state, "search", request.Search))
			return false;

		return true;
	}

	public static bool TryGetFailureCode(
		IReadOnlyDictionary<string, object?>? state,
		out string code)
	{
		code = ReadString(state, "startupError") ?? string.Empty;
		return ReadBoolean(state, "startupReady") && code.Length > 0;
	}

	private static string? ReadPath(
		IReadOnlyDictionary<string, object?>? state,
		string key)
	{
		if (state is null || !state.TryGetValue(key, out var value))
			return null;

		return value switch
		{
			string text when !string.IsNullOrWhiteSpace(text) => PathUtility.Normalize(text),
			JsonElement { ValueKind: JsonValueKind.String } element
				when element.GetString() is { Length: > 0 } text => PathUtility.Normalize(text),
			_ => null
		};
	}

	private static string? ReadString(
		IReadOnlyDictionary<string, object?>? state,
		string key)
	{
		if (state is null || !state.TryGetValue(key, out var value))
			return null;

		return value switch
		{
			string text => text,
			JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
			_ => null
		};
	}

	private static bool StringEquals(
		IReadOnlyDictionary<string, object?>? state,
		string key,
		string expected) =>
		string.Equals(ReadString(state, key), expected, StringComparison.Ordinal);

	private static bool ReadBoolean(
		IReadOnlyDictionary<string, object?>? state,
		string key)
	{
		if (state is null || !state.TryGetValue(key, out var value))
			return false;

		return value switch
		{
			bool boolean => boolean,
			JsonElement { ValueKind: JsonValueKind.True } => true,
			JsonElement { ValueKind: JsonValueKind.False } => false,
			_ => false
		};
	}

	private static string ToToken(DesktopPreviewView view) => view switch
	{
		DesktopPreviewView.Tree => "tree",
		DesktopPreviewView.Content => "content",
		_ => "tree-content"
	};

	private static string ToToken(TreeTextFormat format) => format switch
	{
		TreeTextFormat.Markdown => "markdown",
		TreeTextFormat.Json => "json",
		TreeTextFormat.Xml => "xml",
		_ => "text"
	};
}
