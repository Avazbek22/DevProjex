# DevProjex Command Line

DevProjex can be launched from a terminal to open a project folder, preselect filters, and generate automation reports.

The command line surface is intentionally small. The desktop UI remains the primary experience; CLI options are for startup automation, repeatable checks, and scripts that need a machine-readable project analysis report.

## Usage

```text
DevProjex --path <folder> [options]
DevProjex.exe --path <folder> [options]
devprojex --path <folder> [options]
devprojex.exe --path <folder> [options]
DevProjex <folder> [options]
```

Options with values support both separated and inline assignment forms:

```text
--path <folder>
--path=<folder>
```

## Executable Names And Aliases

| Platform/package | Command |
| --- | --- |
| Windows portable folder | `.\DevProjex.exe` or the full path to `DevProjex.exe`. |
| Windows Microsoft Store/MSIX | `devprojex.exe` through Windows App Execution Alias. The alias starts the packaged DevProjex UI executable; `--no-ui` is a mode of the same app, not a separate CLI binary. |
| Linux installed manually/package | `devprojex` when the executable is installed or symlinked into `PATH`. |
| macOS terminal automation | `devprojex` when a symlink/wrapper is installed into `PATH`, or the direct executable path inside the `.app` bundle. |

Portable builds do not edit `PATH` automatically. Store/MSIX uses the OS-supported App Execution Alias mechanism instead of self-modifying environment variables. DevProjex intentionally ships one desktop executable; automation arguments and silent mode are handled by that executable's startup pipeline.

## Terminal Command Setup

Use **Help → Terminal command** in the desktop app to inspect or enable the terminal command for the current package.

- Windows Store/MSIX: DevProjex relies on the Windows App Execution Alias `devprojex.exe`. The alias is controlled by Windows and can be disabled by the user in Windows Settings.
- Windows portable: DevProjex does not mutate user or machine `PATH`. Start the app with `.\DevProjex.exe` from the publish folder or the full executable path.
- Linux/macOS: DevProjex can create a small user-level wrapper at `~/.local/bin/devprojex`. If `~/.local/bin` is not in `PATH`, the dialog shows the shell profile hint instead of editing profile files automatically.
- If the app is moved, the wrapper can become stale. The same dialog detects that state and repairs the wrapper to the current executable.

## Options

| Option | Description |
| --- | --- |
| `--path <folder>` | Opens a project folder. |
| `<folder>` | Opens a project folder as a positional argument. |
| `--lang <code>` | Sets UI language: `en`, `ru`, `uz`, `tg`, `kk`, `fr`, `de`, `it`. |
| `--report [file]` | Writes a JSON analysis report. If `file` is omitted, DevProjex writes to the default report folder. Use `--report -` to write JSON to stdout. |
| `--report-path <file>` | Writes a JSON analysis report to a specific file. |
| `--report-format json` | Selects the report format. JSON is the v1 format. |
| `--include-root <name>` | Includes one root folder. Can be repeated. |
| `--include-extension <ext>` | Includes one extension. Can be repeated. `cs` and `.cs` are equivalent. |
| `--ignore <name\|none>` | Uses exact ignore options for automation. Can be repeated. |
| `--strict` | Returns a failure exit code when the generated report contains diagnostics such as missing selected roots/extensions or access-denied folders. The report is still written first. |
| `--no-ui`, `--silent` | Runs analysis without showing the window. Requires `--report` or `--report-path`. |
| `--version` | Prints application version and exits. |
| `--help`, `-h`, `/?` | Prints help and exits. |

## Ignore Option Names

```text
smart-ignore
git-ignore
hidden-folders
hidden-files
dot-folders
dot-files
empty-folders
empty-files
extensionless-files
none
```

`--ignore none` means "use an explicit empty ignore set". It is different from omitting `--ignore`, where DevProjex uses the current default ignore behavior.

## Reports

Reports are JSON documents with:

- selected root folders, extensions, and ignore options;
- available root folders and extensions discovered in the project;
- resulting tree summary;
- output metrics for tree/content;
- loading, analysis, and total timing in milliseconds;
- diagnostics and warnings.

If no explicit report path is provided, reports are written to:

```text
<Documents>/DevProjex/reports/devprojex-report-YYYY-MM-DD_HH-mm-ss.json
```

If the documents folder cannot be resolved, DevProjex falls back to the user profile folder, then the system temp folder.

For pipeline-style automation, pass `-` as the report path:

```bash
devprojex --no-ui --path "/home/me/projects/app" --report -
```

In that mode stdout contains only the JSON report. If `--strict` is also used and diagnostics are present, the JSON is still written to stdout before DevProjex returns a failure exit code and writes diagnostic messages to stderr.

## Output Contract

Automation-friendly output is kept strict:

- `stdout`: help text, version text, the generated report path, or the JSON report when `--report -` is used.
- `stderr`: parse errors, invalid command combinations, runtime failures, and cancellation messages.
- no UI is created for `--help`, `--version`, or `--no-ui`.

## Windows Portable EXE Note

The Windows desktop executable is built as a GUI-subsystem app so double-clicking DevProjex does not open an extra console window.

Because of that Windows shell behavior, a plain PowerShell call to the portable GUI executable may return control to the shell before a longer `--no-ui` analysis finishes. For reliable Windows automation, use `Start-Process -Wait` and redirect output explicitly:

```powershell
$process = Start-Process `
  -FilePath ".\DevProjex.exe" `
  -ArgumentList @("--no-ui", "--path", "C:\Projects\App", "--report-path", "C:\Reports\app.json") `
  -Wait `
  -PassThru `
  -NoNewWindow `
  -RedirectStandardOutput ".\devprojex.stdout.txt" `
  -RedirectStandardError ".\devprojex.stderr.txt"

exit $process.ExitCode
```

Framework-dependent builds can also be invoked through the .NET host, which behaves like a normal console command:

```powershell
dotnet .\DevProjex.dll --no-ui --path "C:\Projects\App" --report-path "C:\Reports\app.json"
```

Linux and macOS terminal launches use the published executable directly.

## Exit Codes

| Code | Meaning |
| --- | --- |
| `0` | Success, help, or version output. |
| `1` | Runtime failure, strict-mode diagnostics, unavailable project path, or failed report write. |
| `2` | Invalid arguments or invalid command combination. |
| `130` | Operation canceled. |

## Examples

Open a folder in the UI:

```powershell
DevProjex --path "C:\Projects\App"
```

Open a folder using a positional path:

```bash
devprojex "/home/me/projects/app"
```

Open the UI and write a startup report after the project loads:

```powershell
DevProjex --path "C:\Projects\App" --report
```

Run without UI and write a report:

```bash
devprojex --path "/home/me/projects/app" --no-ui --report
```

Run without UI with exact selection overrides:

```powershell
DevProjex --path "C:\Projects\App" --no-ui --report-path "C:\Reports\app.json" --include-root src --include-extension cs --ignore none
```

Pipe the JSON report to stdout:

```bash
devprojex --no-ui --path "/home/me/projects/app" --report -
```

Fail CI when the selected report contract has warnings:

```bash
devprojex --no-ui --path "/home/me/projects/app" --report ./devprojex-report.json --include-root src --include-extension cs --strict
```

Run with selected ignore options:

```bash
devprojex "/home/me/projects/app" --no-ui --report ./devprojex-report.json --ignore smart-ignore --ignore git-ignore --ignore dot-folders
```
