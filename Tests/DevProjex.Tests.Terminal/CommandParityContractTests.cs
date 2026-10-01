using System.CommandLine;

namespace DevProjex.Tests.Terminal;

public sealed class CommandParityContractTests
{
	[Fact]
	public void DocumentedParityNamesMatchThePublishedCommandCatalogs()
	{
		var repository = PublishedApplicationLocator.FindRepositoryRoot();
		var rows = ReadRows(Path.Combine(repository, "Docs", "CommandLine.md"));
		var cli = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		var tui = new TerminalWorkspaceCommandParser();

		Assert.Equal(
			["Action", "GUI", "TUI", "CLI", "MCP"],
			rows.Header);
		Assert.Equal(13, rows.Items.Count);
		foreach (var row in rows.Items)
		{
			ValidateCliCell(cli, row[3]);
			ValidateTuiCell(tui, row[2]);
		}
	}

	private static void ValidateCliCell(Command root, string cell)
	{
		foreach (var operation in CellOperations(cell))
		{
			if (operation.StartsWith("--", StringComparison.Ordinal))
			{
				Assert.Contains(
					EnumerateCommands(root).SelectMany(command => command.Options),
					option => option.Name == operation);
				continue;
			}
			var command = root;
			foreach (var token in operation.Split(' ', StringSplitOptions.RemoveEmptyEntries))
			{
				command = command.Subcommands.SingleOrDefault(candidate => candidate.Name == token) ??
						  throw new Xunit.Sdk.XunitException(
							  $"Docs/CommandLine.md names missing CLI command '{operation}'.");
			}
		}
	}

	private static void ValidateTuiCell(TerminalWorkspaceCommandParser parser, string cell)
	{
		foreach (var operation in CellOperations(cell))
		{
			var parsed = parser.Parse(operation);
			if (parsed.IsSuccess || parsed.Error?.Code == TerminalWorkspaceCommandErrorCode.MissingArgument)
				continue;
			throw new Xunit.Sdk.XunitException(
				$"Docs/CommandLine.md names missing TUI operation '{operation}'.");
		}
	}

	private static IReadOnlyList<Command> EnumerateCommands(Command root)
	{
		var result = new List<Command>();
		var pending = new Stack<Command>();
		pending.Push(root);
		while (pending.Count > 0)
		{
			var command = pending.Pop();
			result.Add(command);
			foreach (var child in command.Subcommands)
				pending.Push(child);
		}
		return result;
	}

	private static IReadOnlySet<string> CellOperations(string cell)
	{
		if (cell is "direct action" or "none by design")
			return new HashSet<string>(StringComparer.Ordinal);
		return cell.Split("<br>", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
			.Select(value => value.Trim().Trim('`'))
			.ToHashSet(StringComparer.Ordinal);
	}

	private static (string[] Header, IReadOnlyList<string[]> Items) ReadRows(string path)
	{
		var lines = File.ReadAllLines(path);
		var heading = Array.FindIndex(lines, line => line == "## Command parity");
		Assert.True(heading >= 0, "Docs/CommandLine.md is missing the Command parity section.");
		var table = lines.Skip(heading + 1).TakeWhile(line => !line.StartsWith("## ", StringComparison.Ordinal))
			.Where(line => line.StartsWith('|')).ToArray();
		Assert.True(table.Length >= 3, "The Command parity table is missing or incomplete.");
		string[] Parse(string line) => line.Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
		return (Parse(table[0]), table.Skip(2).Select(Parse).ToArray());
	}
}
