using Tomlyn;
using Tomlyn.Model;

namespace DevProjex.Infrastructure.TerminalCommands;

internal sealed record McpCodexUserConfigurationReaderOptions
{
	public Func<string?> CodexHomeProvider { get; init; } =
		() => Environment.GetEnvironmentVariable("CODEX_HOME");
	public Func<string> UserProfileProvider { get; init; } =
		() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
	public Func<string, bool> FileExists { get; init; } = File.Exists;
	public Func<string, string> ReadAllText { get; init; } = File.ReadAllText;
}

internal sealed record McpCodexUserConnection(
	string Command,
	IReadOnlyList<string> Arguments,
	IReadOnlyDictionary<string, string> Environment);

internal sealed record McpCodexUserConfigurationRead(
	bool Succeeded,
	McpCodexUserConnection? Connection);

internal interface IMcpCodexUserConfigurationReader
{
	McpCodexUserConfigurationRead Read();
}

internal sealed class McpCodexUserConfigurationReader(
	McpCodexUserConfigurationReaderOptions? options = null) : IMcpCodexUserConfigurationReader
{
	private readonly McpCodexUserConfigurationReaderOptions _options =
		options ?? new McpCodexUserConfigurationReaderOptions();

	public McpCodexUserConfigurationRead Read()
	{
		try
		{
			var codexHome = _options.CodexHomeProvider();
			if (string.IsNullOrWhiteSpace(codexHome))
				codexHome = Path.Combine(_options.UserProfileProvider(), ".codex");
			if (!Path.IsPathFullyQualified(codexHome))
				return new McpCodexUserConfigurationRead(false, null);

			var configurationPath = Path.Combine(Path.GetFullPath(codexHome), "config.toml");
			if (!_options.FileExists(configurationPath))
				return new McpCodexUserConfigurationRead(true, null);

			var root = TomlSerializer.Deserialize<TomlTable>(_options.ReadAllText(configurationPath));
			if (root is null || !root.TryGetValue("mcp_servers", out var serversValue))
				return new McpCodexUserConfigurationRead(true, null);
			if (serversValue is not TomlTable servers)
				return new McpCodexUserConfigurationRead(false, null);
			if (!servers.TryGetValue("devprojex", out var connectionValue))
				return new McpCodexUserConfigurationRead(true, null);
			if (connectionValue is not TomlTable connection ||
				!TryGetString(connection, "command", out var command) ||
				!TryGetStringArray(connection, "args", out var arguments) ||
				!TryGetEnvironment(connection, out var environment))
			{
				return new McpCodexUserConfigurationRead(false, null);
			}

			return new McpCodexUserConfigurationRead(
				true,
				new McpCodexUserConnection(command, arguments, environment));
		}
		catch (Exception exception) when (exception is
				   IOException or
				   UnauthorizedAccessException or
				   ArgumentException or
				   NotSupportedException or
				   System.Security.SecurityException or
				   TomlException)
		{
			return new McpCodexUserConfigurationRead(false, null);
		}
	}

	private static bool TryGetString(TomlTable table, string key, out string value)
	{
		value = string.Empty;
		if (!table.TryGetValue(key, out var candidate) || candidate is not string text ||
			string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		value = text;
		return true;
	}

	private static bool TryGetStringArray(
		TomlTable table,
		string key,
		out IReadOnlyList<string> values)
	{
		values = [];
		if (!table.TryGetValue(key, out var candidate))
			return true;
		if (candidate is not TomlArray array || array.Any(static value => value is not string))
			return false;
		values = array.Cast<string>().ToArray();
		return true;
	}

	private static bool TryGetEnvironment(
		TomlTable table,
		out IReadOnlyDictionary<string, string> environment)
	{
		environment = new Dictionary<string, string>(StringComparer.Ordinal);
		if (!table.TryGetValue("env", out var candidate))
			return true;
		if (candidate is not TomlTable environmentTable ||
			environmentTable.Any(static pair => pair.Value is not string))
		{
			return false;
		}
		environment = environmentTable.ToDictionary(
			static pair => pair.Key,
			static pair => (string)pair.Value,
			StringComparer.Ordinal);
		return true;
	}
}
