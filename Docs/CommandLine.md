# DevProjex Command Line

DevProjex can be launched from a terminal to open a project folder, preselect filters, generate automation reports, and export tree/content text.

The desktop UI remains the primary experience. CLI options are for startup automation, repeatable checks, machine-readable project analysis reports, and script-friendly text exports.

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
| Windows portable folder | `devprojex` after enabling **Help → Launch from terminal**, or `.\DevProjex.exe` / the full path to `DevProjex.exe` before setup. |
| Windows Microsoft Store/MSIX | `devprojex.exe` through Windows App Execution Alias. The alias starts the packaged DevProjex UI executable; `--no-ui` is a mode of the same app, not a separate CLI binary. |
| Linux installed manually/package | `devprojex` when the executable is installed or symlinked into `PATH`. |
| macOS terminal automation | `devprojex` when a symlink/wrapper is installed into `PATH`, or the direct executable path inside the `.app` bundle. |

Portable builds ask before changing terminal setup. Store/MSIX uses the OS-supported App Execution Alias mechanism instead of self-modifying environment variables. DevProjex intentionally ships one desktop executable; automation arguments and silent mode are handled by that executable's startup pipeline.

## Terminal Command Setup

Use **Help → Launch from terminal** in the desktop app to inspect or enable the terminal command for the current package.

- Windows Store/MSIX: DevProjex relies on the Windows App Execution Alias `devprojex.exe`. The alias is controlled by Windows and can be disabled by the user in Windows Settings.
- Windows portable: DevProjex can create a user-level launcher at `%LOCALAPPDATA%\DevProjex\bin\devprojex.cmd` and add that folder to the current user's `PATH`. It never edits the machine-wide `PATH` and does not require administrator rights.
- Linux/macOS: DevProjex can create a small user-level wrapper at `~/.local/bin/devprojex`. If `~/.local/bin` is not in `PATH`, the dialog shows the shell profile hint instead of editing profile files automatically.
- If the app is moved, the launcher/wrapper can become stale. DevProjex detects that state and repairs it silently on startup when the launcher is managed by DevProjex.

## Options

| Option | Description |
| --- | --- |
| `--path <folder>` | Opens a project folder. |
| `<folder>` | Opens a project folder as a positional argument. |
| `--lang <code>` | Sets UI language: `en`, `ru`, `uz`, `tg`, `kk`, `fr`, `de`, `it`. |
| `--report [file]` | Writes a JSON analysis report. If `file` is omitted, DevProjex writes to the default report folder. Use `--report -` to write JSON to stdout. |
| `--report-path <file>` | Writes a JSON analysis report to a specific file. |
| `--report-format json` | Selects the report format. JSON is the v1 format. |
| `--benchmark <folder>` | Runs the standard project report benchmark against a folder and exits without showing the window. |
| `--benchmark-output <file>` | Writes the detailed benchmark JSON report to a specific file. If omitted, DevProjex writes it under the user's local DevProjex benchmark folder. |
| `--export <mode>` | Exports project text and exits without showing the window. Supported modes: `tree`, `content`, `tree-content`. |
| `--output <file\|->`, `-o <file\|->` | Writes export text to a specific file, or to stdout when `-` is used. If omitted, export writes to stdout. |
| `--export-format ascii\|json\|xml\|md`, `--format ascii\|json\|xml\|md` | Selects tree format for `tree` and `tree-content` exports. Content remains plain text. |
| `--last` | Opens the most recent local project folder in the desktop UI. Cannot be combined with `--path` or a positional folder. |
| `--preview` | Opens preview after the project is loaded in the desktop UI. |
| `--preview-mode tree\|content\|tree-content` | Selects the desktop preview content mode at startup and opens preview. |
| `--tree-format ascii\|json\|xml\|md` | Selects the desktop tree format at startup. This is separate from headless `--format`. |
| `--tree-filter <text>` | Opens the desktop tree filter with the provided query. |
| `--preview-search <text>` | Opens preview search with the provided query. |
| `--include-root <name>`, `--roots <name>` | Includes one root folder. Can be repeated. |
| `--include-extension <ext>`, `--ext <ext>` | Includes one extension. Can be repeated. `cs` and `.cs` are equivalent. |
| `--ignore <name\|none>` | Uses exact ignore options for automation. Can be repeated. |
| `--strict` | Returns a failure exit code when the generated report contains diagnostics such as missing selected roots/extensions or access-denied folders. The report is still written first. |
| `--no-ui`, `--silent` | Runs analysis without showing the window. Without an explicit report or export target, writes the JSON analysis report to stdout. |
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

## Desktop Startup

Desktop startup options make the visible app open directly in a useful state without changing the headless export/report contract:

```bash
devprojex --last --preview
devprojex "/home/me/projects/app" --preview-mode tree-content --tree-format md
devprojex "/home/me/projects/app" --tree-filter Services
devprojex "/home/me/projects/app" --preview-search ProjectAnalysisService
```

`--preview-mode` implies `--preview`. `--preview-search` also implies `--preview` because the command is meant to show the project and search state immediately.

`--tree-filter` and `--preview-search` cannot be combined. The desktop UI intentionally shows only one tree text tool at a time, so the CLI keeps that same rule instead of silently choosing one.

Desktop startup options require either a project path or `--last`. They are not valid with `--no-ui`, `--silent`, or `--export`.

## Reports

Reports are JSON documents with:

- selected root folders, extensions, and ignore options;
- available root folders and extensions discovered in the project;
- resulting tree summary;
- output metrics for tree/content;
- loading, analysis, and total timing in milliseconds;
- diagnostics and warnings.

When `--report` is explicitly provided without a file path, the report is written to:

```text
<Documents>/DevProjex/reports/devprojex-report-YYYY-MM-DD_HH-mm-ss-<unique-id>.json
```

The unique suffix prevents reports created during the same second from overwriting each other.

If the documents folder cannot be resolved, DevProjex falls back to the user profile folder, then the system temp folder.

Headless analysis writes the report directly to stdout by default, so the short form is:

```bash
devprojex --silent --path "/home/me/projects/app"
```

For an explicit stdout target that is convenient in scripts, `-` remains supported:

```bash
devprojex --no-ui --path "/home/me/projects/app" --report -
```

In that mode stdout contains only the JSON report. If `--strict` is also used and diagnostics are present, the JSON is still written to stdout before DevProjex returns a failure exit code and writes diagnostic messages to stderr.

## Benchmark

`--benchmark <folder>` runs one standard project report benchmark profile:

- cold process runs: DevProjex starts itself as a child process with `--no-ui --path <folder> --report -`;
- warm pipeline runs: the same report/analyze pipeline is executed repeatedly inside the current process;
- stdout prints a short human-readable summary;
- a detailed JSON report is saved automatically.

```bash
devprojex --benchmark "/home/me/projects/app"
devprojex --benchmark "C:\Projects\App" --benchmark-output "C:\Reports\devprojex-benchmark.json"
```

The benchmark measures wall time, CPU time, process memory, managed memory and GC counts for warm runs, stdout/report size, exit codes, and captured errors for every run. The JSON report also records application/runtime/OS details and the exact child command line used for cold runs.

`--benchmark` is intentionally one fixed scenario: project report analysis. It cannot be combined with `--path`, report/export options, desktop startup options, selection overrides, `--strict`, `--no-ui`, or `--silent`.

## Exports

Exports are human-readable text payloads that match the app's copy/export behavior:

- `tree`: project tree only;
- `content`: text file contents only;
- `tree-content`: tree followed by file contents.

Export commands run headlessly even when `--no-ui` is omitted:

```bash
devprojex "/home/me/projects/app" --export tree -o -
devprojex "/home/me/projects/app" --export tree-content -o ./context.txt
devprojex "/home/me/projects/app" --export content --roots src --ext cs -o ./src-content.md
```

`--format json`, `--format xml`, or `--format md` changes only the tree part. File contents remain plain text so the result is still easy to paste into tools that expect source context.
`--format` and `--export-format` are valid for `tree` and `tree-content`; `content` exports are always plain text.

JSON tree exports use this structure:

```json
{
  "rootPath": "/home/me/projects/app",
  "tree": {
    "src": {
      "Services": [
        "ProjectService.cs"
      ],
      "/": [
        "App.cs"
      ]
    },
    "EmptyFolder": [],
    "/": [
      "README.md"
    ]
  }
}
```

JSON export uses this tree format: arrays contain files, objects contain subfolders, `/` contains files in the current folder, and `[]` represents an empty folder. `rootPath` is written once and normalized with `/`; in `tree-content` exports, file content headers use relative `/` paths.

When `--output` is omitted, export writes to stdout. When `--output` points to a file, DevProjex creates parent folders when needed, writes UTF-8 without BOM, prints the absolute output path to stdout, and never modifies the opened project folder unless that folder is explicitly chosen as the output location.
When report and export are requested together, `--report-path` and `--output` must point to different files.

## Output Contract

Automation-friendly output is kept strict:

- `stdout`: help text, version text, generated file paths, benchmark summaries, implicit or explicit JSON report payloads, or export payloads.
- `stderr`: parse errors, invalid command combinations, runtime failures, and cancellation messages.
- no UI is created for `--help`, `--version`, `--no-ui`, `--export`, or `--benchmark`.
- only one stdout payload can be produced by one command. Do not combine `--report -` with `--export`, and do not combine stdout export with report output in the same command.

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
| `1` | Runtime failure, strict-mode diagnostics, unavailable project path, or failed report/export write. |
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

Run without UI and print a report:

```bash
devprojex --path "/home/me/projects/app" --no-ui
```

Run without UI with exact selection overrides:

```powershell
DevProjex --path "C:\Projects\App" --no-ui --report-path "C:\Reports\app.json" --roots src --ext cs --ignore none
```

Export tree and content to a file:

```bash
devprojex "/home/me/projects/app" --export tree-content -o ./context.txt --roots src --ext cs --ignore none
```

Print an ASCII tree to stdout:

```bash
devprojex "/home/me/projects/app" --export tree -o -
```

Print a JSON tree to stdout:

```bash
devprojex "/home/me/projects/app" --export tree --format json
```

Print an XML tree to stdout:

```bash
devprojex "/home/me/projects/app" --export tree --format xml
```

Export Markdown tree and content to a file:

```bash
devprojex "/home/me/projects/app" --export tree-content --format md -o ./context.md
```

Pipe the JSON report to stdout:

```bash
devprojex --no-ui --path "/home/me/projects/app" --report -
```

Fail CI when the selected report contract has warnings:

```bash
devprojex --no-ui --path "/home/me/projects/app" --report ./devprojex-report.json --roots src --ext cs --strict
```

Run with selected ignore options:

```bash
devprojex "/home/me/projects/app" --no-ui --report ./devprojex-report.json --ignore smart-ignore --ignore git-ignore --ignore dot-folders
```
