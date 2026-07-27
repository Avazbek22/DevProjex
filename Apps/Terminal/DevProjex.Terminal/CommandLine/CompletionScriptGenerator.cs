using System.CommandLine;

namespace DevProjex.Terminal.CommandLine;

public static class CompletionScriptGenerator
{
	public static string Generate(RootCommand root, string shell)
	{
		ArgumentNullException.ThrowIfNull(root);
		var tokens = CollectTokens(root);
		var words = string.Join(' ', tokens);
		return shell.ToLowerInvariant() switch
		{
			"bash" => $"_devprojex_complete() {{ COMPREPLY=( $(compgen -W '{EscapeSingle(words)}' -- \"${{COMP_WORDS[COMP_CWORD]}}\") ); }}\ncomplete -F _devprojex_complete devprojex",
			"zsh" => $"#compdef devprojex\n_arguments '*:command:({EscapeSingle(words)})'",
			"fish" => string.Join(
				Environment.NewLine,
				tokens.Select(token => $"complete -c devprojex -a '{EscapeSingle(token)}'")),
			"powershell" => BuildPowerShell(tokens),
			_ => throw new ArgumentOutOfRangeException(nameof(shell), shell, null)
		};
	}

	private static IReadOnlyList<string> CollectTokens(Command command)
	{
		var tokens = new HashSet<string>(StringComparer.Ordinal);
		var stack = new Stack<Command>();
		stack.Push(command);
		while (stack.Count > 0)
		{
			var current = stack.Pop();
			foreach (var option in current.Options.Where(static option => !option.Hidden))
			{
				tokens.Add(option.Name);
				foreach (var alias in option.Aliases)
					tokens.Add(alias);
			}

			foreach (var child in current.Subcommands.Where(static command => !command.Hidden))
			{
				tokens.Add(child.Name);
				stack.Push(child);
			}
		}

		return tokens.OrderBy(static token => token, StringComparer.Ordinal).ToArray();
	}

	private static string BuildPowerShell(IReadOnlyList<string> tokens)
	{
		var values = string.Join(", ", tokens.Select(token => $"'{EscapeSingle(token)}'"));
		return $"Register-ArgumentCompleter -Native -CommandName devprojex -ScriptBlock {{ param($wordToComplete) @({values}) | Where-Object {{ $_ -like \"$wordToComplete*\" }} | ForEach-Object {{ [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_) }} }}";
	}

	private static string EscapeSingle(string value) => value.Replace("'", "''");
}
