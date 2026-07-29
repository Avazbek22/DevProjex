using System.CommandLine;

namespace DevProjex.Terminal.CommandLine;

public static class CompletionScriptGenerator
{
	public static string Generate(RootCommand root, string shell)
	{
		ArgumentNullException.ThrowIfNull(root);
		var script = shell.ToLowerInvariant() switch
		{
			"bash" => Bash,
			"zsh" => Zsh,
			"fish" => Fish,
			"powershell" => PowerShell,
			_ => throw new ArgumentOutOfRangeException(nameof(shell), shell, null)
		};
		return script.ReplaceLineEndings("\n").TrimEnd('\n') + "\n";
	}

	private const string Bash =
		"""
		_devprojex_complete() {
		    local command_path
		    command_path="$(command -v devprojex)" || return
		    COMPREPLY=()
		    while IFS= read -r candidate; do
		        COMPREPLY+=("$candidate")
		    done < <("$command_path" dev complete --position "$COMP_POINT" -- "$COMP_LINE")
		}
		complete -F _devprojex_complete devprojex
		""";

	private const string Zsh =
		"""
		#compdef devprojex
		_devprojex_complete() {
		    local command_path
		    local -a candidates
		    command_path="$(whence -p devprojex)" || return
		    candidates=("${(@f)$("$command_path" dev complete --position "$CURSOR" -- "$BUFFER")}")
		    _describe 'DevProjex values' candidates
		}
		compdef _devprojex_complete devprojex
		""";

	private const string Fish =
		"""
		function __devprojex_complete
		    set -l command_path (command -s devprojex)
		    test -n "$command_path"; or return
		    $command_path dev complete --position (commandline -C) -- (commandline)
		end
		complete -c devprojex -f -a '(__devprojex_complete)'
		""";

	private const string PowerShell =
		"""
		Register-ArgumentCompleter -Native -CommandName devprojex -ScriptBlock {
		    param($wordToComplete, $commandAst, $cursorPosition)
		    $commandPath = Get-Command devprojex -CommandType Application -ErrorAction Stop |
		        Select-Object -First 1 -ExpandProperty Source
		    $commandLine = $commandAst.ToString()
		    if ($commandLine.Length -lt $cursorPosition) {
		        $commandLine = $commandLine.PadRight($cursorPosition)
		    }
		    $encodedCommandLine = [Convert]::ToBase64String(
		        [System.Text.Encoding]::UTF8.GetBytes($commandLine))
		    & $commandPath dev complete --position $cursorPosition --base64 -- $encodedCommandLine |
		        ForEach-Object {
		            $value = [string]$_
		            $completionText = if ($value -notmatch '^[\p{L}\p{N}_./:\\=-]+$') {
		                "'" + $value.Replace("'", "''") + "'"
		            } else {
		                $value
		            }
		            [System.Management.Automation.CompletionResult]::new(
		                $completionText, $value, 'ParameterValue', $value)
		        }
		}
		""";
}
