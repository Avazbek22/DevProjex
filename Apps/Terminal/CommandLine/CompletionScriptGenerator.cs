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
		    local candidate
		    local has_filename_candidate=0
		    command_path="$(command -v devprojex)" || return
		    COMPREPLY=()
		    while IFS= read -r -d '' candidate; do
		        COMPREPLY+=("$candidate")
		        if [[ "$candidate" == */ || -e "$candidate" ]]; then
		            has_filename_candidate=1
		        fi
		    done < <("$command_path" dev complete --position "$COMP_POINT" \
		        --position-unit utf8-byte --null \
		        --bash-current-word="$2" -- "$COMP_LINE")
		    if (( has_filename_candidate )); then
		        compopt -o filenames 2>/dev/null || true
		    fi
		}
		complete -F _devprojex_complete devprojex
		""";

	private const string Zsh =
		"""
		#compdef devprojex
		_devprojex_complete() {
		    local command_path
		    local candidate
		    local -a candidates
		    local -a displays
		    command_path="$(whence -p devprojex)" || return
		    while IFS= read -r -d '' candidate; do
		        candidates+=("$candidate")
		        displays+=("${(V)candidate}")
		    done < <("$command_path" dev complete --position "$CURSOR" \
		        --position-unit unicode-scalar --null -- "$BUFFER")
		    compadd -d displays -a candidates
		}
		compdef _devprojex_complete devprojex
		""";

	private const string Fish =
		"""
		function __devprojex_complete
		    set -l command_path (command -s devprojex)
		    test -n "$command_path"; or return
		    $command_path dev complete --position (commandline -C) \
		        --position-unit unicode-scalar --null -- (commandline)
		end
		complete -c devprojex -f -a '(__devprojex_complete | string split0)'
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
		    $encodedWorkingDirectory = [Convert]::ToBase64String(
		        [System.Text.Encoding]::UTF8.GetBytes($PWD.Path))
		    $strictUtf8 = [System.Text.UTF8Encoding]::new($false, $true)
		    & $commandPath dev complete --position $cursorPosition --base64 `
		        --working-directory-base64 $encodedWorkingDirectory -- $encodedCommandLine |
		        ForEach-Object {
		            $value = $strictUtf8.GetString(
		                [Convert]::FromBase64String([string]$_))
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
