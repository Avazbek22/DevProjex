using System.CommandLine;
using System.Runtime.CompilerServices;

namespace DevProjex.Terminal.CommandLine;

internal static class CliExamplesRegistry
{
	private static readonly ConditionalWeakTable<Command, Examples> ExamplesByCommand = new();

	public static void Set(Command command, params string[] examples)
	{
		ArgumentNullException.ThrowIfNull(command);
		ArgumentNullException.ThrowIfNull(examples);
		ExamplesByCommand.AddOrUpdate(
			command,
			new Examples(examples
				.Where(static example => !string.IsNullOrWhiteSpace(example))
				.ToArray()));
	}

	public static IReadOnlyList<string> Get(Command command, string commandPath)
	{
		ArgumentNullException.ThrowIfNull(command);
		ArgumentException.ThrowIfNullOrWhiteSpace(commandPath);
		return ExamplesByCommand.TryGetValue(command, out var configured) &&
		       configured.Values.Count > 0
			? configured.Values
			: [$"{commandPath} --help"];
	}

	private sealed record Examples(IReadOnlyList<string> Values);
}
