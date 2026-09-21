using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DevProjex.Application.Services;

public enum McpConnectionClient
{
	ClaudeCode,
	Codex,
	Cursor,
	VsCode,
	Json
}

public enum McpConnectionMode
{
	Standard,
	Live
}

public static class McpConnectionFragmentGenerator
{
	public const string AppImageExtractAndRunVariable = "APPIMAGE_EXTRACT_AND_RUN";

	public static IReadOnlyDictionary<string, string> GetRequiredServerEnvironment()
	{
		var enabledByEnvironment = string.Equals(
			Environment.GetEnvironmentVariable(AppImageExtractAndRunVariable),
			"1",
			StringComparison.Ordinal);
		var enabledByArgument = Environment.GetCommandLineArgs().Contains(
			"--appimage-extract-and-run",
			StringComparer.Ordinal);
		return enabledByEnvironment || enabledByArgument
			? new Dictionary<string, string>(StringComparer.Ordinal)
			{
				[AppImageExtractAndRunVariable] = "1"
			}
			: new Dictionary<string, string>(StringComparer.Ordinal);
	}

	public static string Generate(
		McpConnectionClient client,
		McpConnectionMode mode,
		string executablePath,
		string projectRoot)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		if (!IsAbsolutePath(executablePath))
			throw new ArgumentException("The DevProjex executable path must be absolute.", nameof(executablePath));
		if (!IsAbsolutePath(projectRoot))
			throw new ArgumentException("The project root must be absolute.", nameof(projectRoot));
		ValidateSingleLine(executablePath, nameof(executablePath));
		ValidateSingleLine(projectRoot, nameof(projectRoot));

		return client switch
		{
			McpConnectionClient.ClaudeCode => BuildClaudeCode(mode, executablePath, projectRoot),
			McpConnectionClient.Codex => BuildCodex(mode, executablePath, projectRoot),
			McpConnectionClient.Cursor => BuildJson(mode, executablePath, projectRoot),
			McpConnectionClient.VsCode => BuildVsCode(mode, executablePath, projectRoot),
			McpConnectionClient.Json => BuildJson(mode, executablePath, projectRoot),
			_ => throw new ArgumentOutOfRangeException(nameof(client), client, null)
		};
	}

	private static string BuildClaudeCode(
		McpConnectionMode mode,
		string executablePath,
		string projectRoot)
	{
		Func<string, string> quoteArgument = IsWindowsAbsolutePath(executablePath)
			? QuotePowerShellArgument
			: QuotePosixShellArgument;
		var builder = new StringBuilder("cd ");
		builder.Append(quoteArgument(projectRoot));
		builder.Append(" && claude mcp add --scope local devprojex");
		AppendClaudeEnvironment(builder, GetRequiredServerEnvironment());
		builder.Append(" -- ");
		builder.Append(quoteArgument(executablePath));
		builder.Append(" mcp --root ");
		builder.Append(quoteArgument(projectRoot));
		if (mode == McpConnectionMode.Live)
			builder.Append(" --live");
		return builder.ToString();
	}

	private static string BuildCodex(
		McpConnectionMode mode,
		string executablePath,
		string projectRoot)
	{
		var arguments = BuildArguments(mode, projectRoot);
		var builder = new StringBuilder()
			.Append("[mcp_servers.devprojex]").AppendLine()
			.Append("command = ").Append(ToTomlString(executablePath)).AppendLine()
			.Append("args = [").Append(string.Join(", ", arguments.Select(ToTomlString))).Append(']');
		var environment = GetRequiredServerEnvironment();
		if (environment.Count > 0)
		{
			builder.AppendLine().Append("[mcp_servers.devprojex.env]");
			foreach (var pair in environment)
				builder.AppendLine().Append(pair.Key).Append(" = ").Append(ToTomlString(pair.Value));
		}
		return builder.ToString();
	}

	private static string BuildJson(
		McpConnectionMode mode,
		string executablePath,
		string projectRoot)
	{
		var environment = GetRequiredServerEnvironment();
		var server = new Dictionary<string, object?>(StringComparer.Ordinal)
		{
			["command"] = executablePath,
			["args"] = BuildArguments(mode, projectRoot)
		};
		if (environment.Count > 0)
			server["env"] = environment;
		var payload = new
		{
			mcpServers = new
			{
				devprojex = server
			}
		};
		return JsonSerializer.Serialize(payload, new JsonSerializerOptions
		{
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
		});
	}

	private static string BuildVsCode(
		McpConnectionMode mode,
		string executablePath,
		string projectRoot)
	{
		var environment = GetRequiredServerEnvironment();
		var server = new Dictionary<string, object?>(StringComparer.Ordinal)
		{
			["type"] = "stdio",
			["command"] = executablePath,
			["args"] = BuildArguments(mode, projectRoot)
		};
		if (environment.Count > 0)
			server["env"] = environment;
		var payload = new
		{
			servers = new
			{
				devprojex = server
			}
		};
		return JsonSerializer.Serialize(payload, new JsonSerializerOptions
		{
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
		});
	}

	private static string[] BuildArguments(McpConnectionMode mode, string projectRoot) =>
		mode == McpConnectionMode.Live
			? ["mcp", "--root", projectRoot, "--live"]
			: ["mcp", "--root", projectRoot];

	private static void AppendClaudeEnvironment(
		StringBuilder builder,
		IReadOnlyDictionary<string, string> environment)
	{
		foreach (var pair in environment)
		{
			builder.Append(" -e ");
			builder.Append(pair.Key);
			builder.Append('=');
			builder.Append(pair.Value);
		}
	}

	private static string QuotePowerShellArgument(string value)
	{
		if (value.IndexOfAny(['"', '$', '`']) >= 0)
			return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
		return $"\"{value}\"";
	}

	private static string QuotePosixShellArgument(string value)
	{
		var builder = new StringBuilder(value.Length + 2).Append('"');
		foreach (var character in value)
		{
			if (character is '\\' or '"' or '$' or '`')
				builder.Append('\\');
			builder.Append(character);
		}
		return builder.Append('"').ToString();
	}

	private static string ToTomlString(string value)
	{
		var builder = new StringBuilder(value.Length + 2).Append('"');
		foreach (var character in value)
		{
			builder.Append(character switch
			{
				'\\' => "\\\\",
				'"' => "\\\"",
				'\b' => "\\b",
				'\t' => "\\t",
				'\n' => "\\n",
				'\f' => "\\f",
				'\r' => "\\r",
				_ => character.ToString()
			});
		}
		return builder.Append('"').ToString();
	}

	private static bool IsAbsolutePath(string value) =>
		value[0] == '/' ||
		IsWindowsAbsolutePath(value);

	private static bool IsWindowsAbsolutePath(string value) =>
		value.StartsWith("\\\\", StringComparison.Ordinal) ||
		value.Length >= 3 &&
		char.IsAsciiLetter(value[0]) &&
		value[1] == ':' &&
		value[2] is '\\' or '/';

	private static void ValidateSingleLine(string value, string parameterName)
	{
		if (value.IndexOfAny(['\r', '\n', '\0']) >= 0)
			throw new ArgumentException("Connection fragment paths must be single-line text.", parameterName);
	}
}
