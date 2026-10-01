using System.Text.Json;

namespace DevProjex.Infrastructure.TerminalCommands;

internal sealed record McpClaudeUserConfigurationReaderOptions
{
	public Func<string?> ConfigurationDirectoryProvider { get; init; } =
		() => Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
	public Func<string> UserProfileProvider { get; init; } =
		() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
	public Func<string, bool> FileExists { get; init; } = File.Exists;
	public Func<string, string> ReadAllText { get; init; } = File.ReadAllText;
}

internal sealed record McpClaudeUserConnection(
	string Command,
	IReadOnlyList<string> Arguments,
	IReadOnlyDictionary<string, string> Environment,
	string RawJson);

internal sealed record McpClaudeUserConfigurationRead(
	bool Succeeded,
	McpClaudeUserConnection? Connection);

internal interface IMcpClaudeUserConfigurationReader
{
	McpClaudeUserConfigurationRead Read(string projectRoot);
}

internal sealed class McpClaudeUserConfigurationReader(
	McpClaudeUserConfigurationReaderOptions? options = null) : IMcpClaudeUserConfigurationReader
{
	private readonly McpClaudeUserConfigurationReaderOptions _options =
		options ?? new McpClaudeUserConfigurationReaderOptions();

	public McpClaudeUserConfigurationRead Read(string projectRoot)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		try
		{
			var configurationDirectory = _options.ConfigurationDirectoryProvider();
			var configurationPath = string.IsNullOrWhiteSpace(configurationDirectory)
				? Path.Combine(_options.UserProfileProvider(), ".claude.json")
				: Path.Combine(configurationDirectory, ".claude.json");
			if (!Path.IsPathFullyQualified(configurationPath))
				return new McpClaudeUserConfigurationRead(false, null);
			configurationPath = Path.GetFullPath(configurationPath);
			if (!_options.FileExists(configurationPath))
				return new McpClaudeUserConfigurationRead(true, null);

			using var document = JsonDocument.Parse(_options.ReadAllText(configurationPath));
			if (!document.RootElement.TryGetProperty("projects", out var projects))
				return new McpClaudeUserConfigurationRead(true, null);
			if (projects.ValueKind != JsonValueKind.Object)
				return new McpClaudeUserConfigurationRead(false, null);
			if (!TryFindProject(projects, projectRoot, out var project))
				return new McpClaudeUserConfigurationRead(true, null);
			if (!project.TryGetProperty("mcpServers", out var servers))
				return new McpClaudeUserConfigurationRead(true, null);
			if (servers.ValueKind != JsonValueKind.Object)
				return new McpClaudeUserConfigurationRead(false, null);
			if (!servers.TryGetProperty("devprojex", out var connection))
				return new McpClaudeUserConfigurationRead(true, null);
			if (!TryReadConnection(connection, out var result))
				return new McpClaudeUserConfigurationRead(false, null);
			return new McpClaudeUserConfigurationRead(true, result);
		}
		catch (Exception exception) when (exception is
				   IOException or
				   UnauthorizedAccessException or
				   ArgumentException or
				   NotSupportedException or
				   System.Security.SecurityException or
				   JsonException)
		{
			return new McpClaudeUserConfigurationRead(false, null);
		}
	}

	private static bool TryFindProject(
		JsonElement projects,
		string projectRoot,
		out JsonElement project)
	{
		if (projects.TryGetProperty(projectRoot, out project))
			return project.ValueKind == JsonValueKind.Object;
		foreach (var property in projects.EnumerateObject())
		{
			if (!PathsEqual(property.Name, projectRoot))
				continue;
			project = property.Value;
			return project.ValueKind == JsonValueKind.Object;
		}
		project = default;
		return false;
	}

	private static bool TryReadConnection(
		JsonElement connection,
		out McpClaudeUserConnection result)
	{
		result = default!;
		if (connection.ValueKind != JsonValueKind.Object ||
			!connection.TryGetProperty("command", out var commandElement) ||
			commandElement.ValueKind != JsonValueKind.String ||
			string.IsNullOrWhiteSpace(commandElement.GetString()) ||
			!connection.TryGetProperty("args", out var argumentsElement) ||
			argumentsElement.ValueKind != JsonValueKind.Array)
		{
			return false;
		}
		var arguments = new List<string>();
		foreach (var argument in argumentsElement.EnumerateArray())
		{
			if (argument.ValueKind != JsonValueKind.String)
				return false;
			arguments.Add(argument.GetString() ?? string.Empty);
		}
		var environment = new Dictionary<string, string>(StringComparer.Ordinal);
		if (connection.TryGetProperty("env", out var environmentElement))
		{
			if (environmentElement.ValueKind != JsonValueKind.Object)
				return false;
			foreach (var property in environmentElement.EnumerateObject())
			{
				if (property.Value.ValueKind != JsonValueKind.String)
					return false;
				environment[property.Name] = property.Value.GetString() ?? string.Empty;
			}
		}
		result = new McpClaudeUserConnection(
			commandElement.GetString()!,
			arguments,
			environment,
			connection.GetRawText());
		return true;
	}

	private static bool PathsEqual(string left, string right)
	{
		try
		{
			return string.Equals(
				Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
				Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
				OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
		{
			return false;
		}
	}
}
