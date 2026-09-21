using System.Text;
using System.Text.RegularExpressions;
using DevProjex.Application.Services;
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
	IReadOnlyDictionary<string, string> Environment,
	bool HasExtendedFields = false);

internal sealed record McpCodexUserConfigurationRead(
	bool Succeeded,
	McpCodexUserConnection? Connection,
	string? ConfigurationPath = null,
	string? SourceText = null);

internal sealed record McpCodexUserConfigurationWrite(
	bool Succeeded,
	string? Error = null);

internal interface IMcpCodexUserConfigurationReader
{
	McpCodexUserConfigurationRead Read();
}

internal interface IMcpCodexUserConfigurationStore : IMcpCodexUserConfigurationReader
{
	Task<McpCodexUserConfigurationWrite> UpdateConnectionAsync(
		McpCodexUserConfigurationRead snapshot,
		string command,
		IReadOnlyList<string> arguments,
		IReadOnlyDictionary<string, string> requiredEnvironment,
		CancellationToken cancellationToken);

	Task<bool> RestoreAsync(
		McpCodexUserConfigurationRead snapshot,
		CancellationToken cancellationToken);
}

internal sealed class McpCodexUserConfigurationReader(
	McpCodexUserConfigurationReaderOptions? options = null) : IMcpCodexUserConfigurationStore
{
	private static readonly Regex ServerHeader = new(
		@"(?m)^[ \t]*\[mcp_servers\.devprojex\][ \t]*(?:#.*)?$",
		RegexOptions.CultureInvariant);
	private static readonly Regex AnyHeader = new(
		@"(?m)^[ \t]*\[",
		RegexOptions.CultureInvariant);
	private static readonly Regex CommandLine = new(
		@"(?m)^(?<prefix>[ \t]*command[ \t]*=[ \t]*)(?<value>[^\r\n]*)(?<ending>\r?)$",
		RegexOptions.CultureInvariant);
	private static readonly Regex ArgumentsLine = new(
		@"(?m)^(?<prefix>[ \t]*args[ \t]*=[ \t]*)(?<value>[^\r\n]*)(?<ending>\r?)$",
		RegexOptions.CultureInvariant);
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

			var source = _options.ReadAllText(configurationPath);
			var root = TomlSerializer.Deserialize<TomlTable>(source);
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
				new McpCodexUserConnection(
					command,
					arguments,
					environment,
					connection.Keys.Any(static key => key is not ("command" or "args" or "env"))),
				configurationPath,
				source);
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

	public async Task<McpCodexUserConfigurationWrite> UpdateConnectionAsync(
		McpCodexUserConfigurationRead snapshot,
		string command,
		IReadOnlyList<string> arguments,
		IReadOnlyDictionary<string, string> requiredEnvironment,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentException.ThrowIfNullOrWhiteSpace(command);
		ArgumentNullException.ThrowIfNull(arguments);
		ArgumentNullException.ThrowIfNull(requiredEnvironment);
		if (!snapshot.Succeeded ||
			string.IsNullOrWhiteSpace(snapshot.ConfigurationPath) ||
			snapshot.SourceText is null)
		{
			return new McpCodexUserConfigurationWrite(false, "The Codex configuration snapshot is unavailable.");
		}
		try
		{
			var updated = UpdateSource(
				snapshot.SourceText,
				command,
				arguments,
				requiredEnvironment);
			var backupPath = snapshot.ConfigurationPath + ".devprojex.bak";
			await WriteTextAtomicallyAsync(backupPath, snapshot.SourceText, cancellationToken)
				.ConfigureAwait(false);
			await WriteTextAtomicallyAsync(snapshot.ConfigurationPath, updated, cancellationToken)
				.ConfigureAwait(false);
			return new McpCodexUserConfigurationWrite(true);
		}
		catch (Exception exception) when (exception is
				   IOException or
				   UnauthorizedAccessException or
				   ArgumentException or
				   NotSupportedException or
				   System.Security.SecurityException or
				   InvalidDataException)
		{
			return new McpCodexUserConfigurationWrite(false, exception.Message);
		}
	}

	public async Task<bool> RestoreAsync(
		McpCodexUserConfigurationRead snapshot,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(snapshot.ConfigurationPath) || snapshot.SourceText is null)
			return false;
		try
		{
			await WriteTextAtomicallyAsync(snapshot.ConfigurationPath, snapshot.SourceText, cancellationToken)
				.ConfigureAwait(false);
			return true;
		}
		catch (Exception exception) when (exception is
				   IOException or
				   UnauthorizedAccessException or
				   ArgumentException or
				   NotSupportedException or
				   System.Security.SecurityException)
		{
			return false;
		}
	}

	private static string UpdateSource(
		string source,
		string command,
		IReadOnlyList<string> arguments,
		IReadOnlyDictionary<string, string> requiredEnvironment)
	{
		var header = ServerHeader.Match(source);
		if (!header.Success)
			throw new InvalidDataException("The Codex devprojex table is missing.");
		var contentStart = header.Index + header.Length;
		var nextHeader = AnyHeader.Match(source, contentStart);
		var contentEnd = nextHeader.Success ? nextHeader.Index : source.Length;
		var section = source[contentStart..contentEnd];
		if (!CommandLine.IsMatch(section) || !ArgumentsLine.IsMatch(section))
			throw new InvalidDataException("The Codex devprojex table is incomplete.");
		section = ReplaceAssignment(CommandLine, section, ToTomlString(command));
		section = ReplaceAssignment(
			ArgumentsLine,
			section,
			$"[{string.Join(", ", arguments.Select(ToTomlString))}]");
		var updated = source[..contentStart] + section + source[contentEnd..];
		return requiredEnvironment.Count == 0
			? updated
			: MergeEnvironment(updated, requiredEnvironment);
	}

	private static string ReplaceAssignment(Regex expression, string source, string replacement) =>
		expression.Replace(
			source,
			match => match.Groups["prefix"].Value +
				replacement +
				PreservedLineSuffix(match.Groups["value"].Value) +
				match.Groups["ending"].Value,
			1);

	private static string PreservedLineSuffix(string value)
	{
		var inBasicString = false;
		var inLiteralString = false;
		var escaped = false;
		for (var index = 0; index < value.Length; index++)
		{
			var character = value[index];
			if (inBasicString)
			{
				if (escaped)
				{
					escaped = false;
					continue;
				}
				if (character == '\\')
				{
					escaped = true;
					continue;
				}
				if (character == '"')
					inBasicString = false;
				continue;
			}
			if (inLiteralString)
			{
				if (character == '\'')
					inLiteralString = false;
				continue;
			}
			if (character == '"')
			{
				inBasicString = true;
				continue;
			}
			if (character == '\'')
			{
				inLiteralString = true;
				continue;
			}
			if (character != '#')
				continue;
			var suffixStart = index;
			while (suffixStart > 0 && value[suffixStart - 1] is ' ' or '\t')
				suffixStart--;
			return value[suffixStart..];
		}
		var trailingStart = value.Length;
		while (trailingStart > 0 && value[trailingStart - 1] is ' ' or '\t')
			trailingStart--;
		return value[trailingStart..];
	}

	private static string MergeEnvironment(
		string source,
		IReadOnlyDictionary<string, string> requiredEnvironment)
	{
		const string environmentHeader = "[mcp_servers.devprojex.env]";
		var headerIndex = source.IndexOf(environmentHeader, StringComparison.Ordinal);
		if (headerIndex < 0)
		{
			var suffix = source.EndsWith('\n') ? string.Empty : Environment.NewLine;
			var environment = string.Join(
				Environment.NewLine,
				requiredEnvironment.Select(static pair => $"{pair.Key} = {ToTomlString(pair.Value)}"));
			return source + suffix + environmentHeader + Environment.NewLine + environment + Environment.NewLine;
		}
		var contentStart = headerIndex + environmentHeader.Length;
		var nextHeader = AnyHeader.Match(source, contentStart);
		var contentEnd = nextHeader.Success ? nextHeader.Index : source.Length;
		var section = source[contentStart..contentEnd];
		foreach (var pair in requiredEnvironment)
		{
			var variable = new Regex(
				$@"(?m)^(?<prefix>[ \t]*{Regex.Escape(pair.Key)}[ \t]*=[ \t]*).*$",
				RegexOptions.CultureInvariant);
			if (variable.IsMatch(section))
			{
				section = variable.Replace(
					section,
					match => match.Groups["prefix"].Value + ToTomlString(pair.Value),
					1);
			}
			else
			{
				var separator = section.EndsWith('\n') || section.Length == 0
					? string.Empty
					: Environment.NewLine;
				section += separator + pair.Key + " = " + ToTomlString(pair.Value) + Environment.NewLine;
			}
		}
		return source[..contentStart] + section + source[contentEnd..];
	}

	private static async Task WriteTextAtomicallyAsync(
		string path,
		string text,
		CancellationToken cancellationToken)
	{
		await AtomicFileOutput.WriteAsync(
			path,
			overwrite: true,
			async (stream, token) =>
			{
				await using var writer = new StreamWriter(
					stream,
					new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
					bufferSize: 4096,
					leaveOpen: true);
				await writer.WriteAsync(text.AsMemory(), token).ConfigureAwait(false);
				await writer.FlushAsync(token).ConfigureAwait(false);
			},
			cancellationToken).ConfigureAwait(false);
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
