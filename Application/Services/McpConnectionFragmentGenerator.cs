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
		var builder = new StringBuilder("claude mcp add devprojex -- ");
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
		return "[mcp_servers.devprojex]" + Environment.NewLine +
			   $"command = {ToTomlString(executablePath)}" + Environment.NewLine +
			   $"args = [{string.Join(", ", arguments.Select(ToTomlString))}]";
	}

	private static string BuildJson(
		McpConnectionMode mode,
		string executablePath,
		string projectRoot)
	{
		var payload = new
		{
			mcpServers = new
			{
				devprojex = new
				{
					command = executablePath,
					args = BuildArguments(mode, projectRoot)
				}
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
		var payload = new
		{
			servers = new
			{
				devprojex = new
				{
					type = "stdio",
					command = executablePath,
					args = BuildArguments(mode, projectRoot)
				}
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
