using System.CommandLine;

namespace DevProjex.Tests.Terminal;

public sealed class CommandParityContractTests
{
	private static readonly DateOnly ExpectedTuiParityDate = new(2026, 10, 19);
	private static readonly IReadOnlySet<string> ExpectedTuiOperations = new HashSet<string>(
		["open", "select", "profile load", "profile show", "profile reset", "related"],
		StringComparer.Ordinal);

	[Fact]
	public void DocumentedParityNamesMatchThePublishedCommandCatalogs()
	{
		var repository = PublishedApplicationLocator.FindRepositoryRoot();
		var rows = ReadRows(Path.Combine(repository, "Docs", "CommandLine.md"));
		var cli = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		var tui = PublishedTuiOperations();

		Assert.Equal(
			["Action", "GUI", "TUI", "CLI", "MCP"],
			rows.Header);
		Assert.Equal(12, rows.Items.Count);
		foreach (var row in rows.Items)
		{
			ValidateCliCell(cli, row[3]);
			ValidateTuiCell(tui, row[2]);
		}
		Assert.All(
			ExpectedTuiOperations,
			expected => Assert.Contains(rows.Items, row => CellOperations(row[2]).Contains(expected)));
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

	private static void ValidateTuiCell(IReadOnlySet<string> published, string cell)
	{
		foreach (var operation in CellOperations(cell))
		{
			if (published.Contains(operation))
				continue;
			if (!ExpectedTuiOperations.Contains(operation))
			{
				throw new Xunit.Sdk.XunitException(
					$"Docs/CommandLine.md names missing TUI operation '{operation}'.");
			}
			Assert.True(
				DateOnly.FromDateTime(DateTime.UtcNow) <= ExpectedTuiParityDate,
				$"Expected TUI operation '{operation}' is still absent after {ExpectedTuiParityDate:yyyy-MM-dd}.");
		}
	}

	private static IReadOnlySet<string> PublishedTuiOperations()
	{
		var result = new HashSet<string>(StringComparer.Ordinal);
		foreach (var definition in TerminalWorkspaceCommandCatalog.All)
		{
			result.Add(definition.Token);
			foreach (var alternative in definition.Syntax.Split('|', StringSplitOptions.TrimEntries))
			{
				var syntax = alternative.Split(' ', StringSplitOptions.RemoveEmptyEntries);
				if (syntax.Length > 1 &&
					syntax[0] == definition.Token &&
					!syntax[1].StartsWith('<') &&
					!syntax[1].StartsWith('['))
				{
					result.Add($"{definition.Token} {syntax[1]}");
				}
			}
		}
		return result;
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
