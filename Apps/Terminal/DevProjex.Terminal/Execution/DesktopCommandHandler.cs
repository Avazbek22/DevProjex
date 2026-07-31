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
	private static readonly JsonSerializerOptions MachineJsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	private readonly DesktopControlClient _client = client ?? new DesktopControlClient();
	private readonly DesktopProcessLauncher _launcher = launcher ?? new DesktopProcessLauncher();

	public async Task<int> OpenAsync(
		DesktopOpenRequest request,
		CancellationToken cancellationToken)
	{
		if (request.NewWindow)
		{
			var launched = await _launcher.LaunchAsync(request, cancellationToken).ConfigureAwait(false);
			IReadOnlyDictionary<string, object?>? state;
			try
			{
				state = await WaitForLaunchedInstanceAsync(
					launched.ProcessId,
					request,
					cancellationToken).ConfigureAwait(false);
			}
			catch
			{
				DesktopInstanceRegistry.TryDelete(launched.RequestPath);
				throw;
			}
			WriteOutput(ResolveAcceptedProjectPath(request, state));
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
			IReadOnlyDictionary<string, object?>? state;
			try
			{
				state = await WaitForLaunchedInstanceAsync(
					launched.ProcessId,
					request,
					cancellationToken).ConfigureAwait(false);
			}
			catch
			{
				DesktopInstanceRegistry.TryDelete(launched.RequestPath);
				throw;
			}
			WriteOutput(ResolveAcceptedProjectPath(request, state));
			return CommandLineExitCodes.Success;
		}

		var response = await _client.SendAsync(
			matching,
			"open",
			request,
			request.WaitForCompletion ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(10),
			cancellationToken).ConfigureAwait(false);
		EnsureSuccess(response);
		WriteOutput(ResolveAcceptedProjectPath(request, response.State));
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
					kind = "devprojex-ui-instances",
					instances = instances.Select(static instance => new
					{
						instance.ProtocolVersion,
						instance.InstanceId,
						instance.ProcessId,
						instance.ProcessStartTimeUtcTicks,
						projectPath = NormalizeMachinePath(instance.ProjectPath),
						instance.LastActiveUtc,
						instance.Transport,
						instance.Endpoint
					})
				},
				MachineJsonOptions));
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
		environment.Output.WriteLine(JsonSerializer.Serialize(
			new
			{
				response.ProtocolVersion,
				response.RequestId,
				response.Ok,
				state = NormalizeState(response.State),
				response.Error
			},
			MachineJsonOptions));

		return CommandLineExitCodes.Success;
	}

	private static IReadOnlyDictionary<string, object?>? NormalizeState(
		IReadOnlyDictionary<string, object?>? state)
	{
		if (state is null)
			return null;

		return state.ToDictionary(
			static pair => pair.Key,
			static pair => NormalizeStateValue(pair.Key, pair.Value),
			StringComparer.Ordinal);
	}

	private static object? NormalizeStateValue(string key, object? value)
	{
		if (!string.Equals(key, "projectPath", StringComparison.Ordinal))
			return value;

		return value switch
		{
			string path => NormalizeMachinePath(path),
			JsonElement { ValueKind: JsonValueKind.String } element =>
				NormalizeMachinePath(element.GetString()),
			_ => value
		};
	}

	private static string? NormalizeMachinePath(string? path) =>
		path?.Replace('\\', '/');

	private async Task<IReadOnlyDictionary<string, object?>?> WaitForLaunchedInstanceAsync(
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
				if (DesktopOpenReadiness.TryGetFailureCode(response.State, out var failureCode))
				{
					throw new DesktopControlException(
						failureCode,
						"DevProjex Desktop could not apply the startup request.");
				}
				if (!request.WaitForCompletion &&
				    request.Selection?.GitMode != GitFilteringMode.TrackedFilesOnly &&
				    (!request.UseLastProject ||
				     DesktopOpenReadiness.TryGetProjectPath(response.State, out _)))
				{
					return response.State;
				}
				if (DesktopOpenReadiness.IsApplied(request, response.State))
					return response.State;
			}

			await Task.Delay(100, cancellationToken).ConfigureAwait(false);
		}

		throw new DesktopControlException(
			"DPX-DESKTOP-TIMEOUT",
			"DevProjex Desktop did not become ready before the timeout.");
	}

	private static string ResolveAcceptedProjectPath(
		DesktopOpenRequest request,
		IReadOnlyDictionary<string, object?>? state)
	{
		if (!string.IsNullOrWhiteSpace(request.ProjectPath))
			return PathUtility.Normalize(request.ProjectPath);
		if (DesktopOpenReadiness.TryGetProjectPath(state, out var projectPath))
			return projectPath;

		throw new DesktopControlException(
			"DPX-DESKTOP-PROJECT-OPEN-FAILED",
			"DevProjex Desktop did not report the accepted project path.");
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
		if (request.Selection?.GitMode is { } gitMode &&
		    (!StringEquals(state, "gitMode", ToToken(gitMode)) ||
		     (gitMode == GitFilteringMode.TrackedFilesOnly &&
		      !ReadBoolean(state, "trackedGitReady"))))
		{
			return false;
		}

		return true;
	}

	public static bool TryGetFailureCode(
		IReadOnlyDictionary<string, object?>? state,
		out string code)
	{
		code = ReadString(state, "startupError") ?? string.Empty;
		return ReadBoolean(state, "startupReady") && code.Length > 0;
	}

	public static bool TryGetProjectPath(
		IReadOnlyDictionary<string, object?>? state,
		out string projectPath)
	{
		projectPath = ReadPath(state, "projectPath") ?? string.Empty;
		return projectPath.Length > 0 && ReadBoolean(state, "projectLoaded");
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
		DesktopPreviewView.TreeContent => "tree-content",
		_ => throw new ArgumentOutOfRangeException(nameof(view), view, null)
	};

	private static string ToToken(TreeTextFormat format) => format switch
	{
		TreeTextFormat.Ascii => "text",
		TreeTextFormat.Markdown => "markdown",
		TreeTextFormat.Json => "json",
		TreeTextFormat.Xml => "xml",
		_ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
	};

	private static string ToToken(GitFilteringMode mode) => mode switch
	{
		GitFilteringMode.None => "none",
		GitFilteringMode.RespectGitIgnore => "gitignore",
		GitFilteringMode.TrackedFilesOnly => "tracked",
		_ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
	};
}
