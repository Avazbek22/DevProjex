# DevProjex Command Line

DevProjex can be launched from a terminal to open a project folder, preselect filters, generate automation reports, export tree/content text, and create project copies as folders or ZIP archives.

The desktop UI remains the primary experience. CLI options are for startup automation, repeatable checks, machine-readable project analysis reports, script-friendly text exports, and reproducible project copies.

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
| `--lang <code>` | Sets UI language: `en`, `ru`, `es`, `pt`, `pt-pt`, `de`, `fr`, `it`, `tg`, `uz`, `kk`. |
| `--report [file]` | Writes a JSON analysis report. If `file` is omitted, DevProjex writes to the default report folder. Use `--report -` to write JSON to stdout. |
| `--report-path <file>` | Writes a JSON analysis report to a specific file. |
| `--report-format json` | Selects the report format. JSON is the v1 format. |
| `--benchmark <folder>` | Runs the standard project report benchmark against a folder and exits without showing the window. |
| `--benchmark-ui <folder>` | Runs the standard desktop UI benchmark against a folder. It opens real UI child processes, runs a deterministic preview/search/filter scenario, then exits. |
| `--benchmark-output <file>` | Writes the detailed benchmark JSON report to a specific file. If omitted, DevProjex writes it under the user's local DevProjex benchmark folder. Applies to `--benchmark` and `--benchmark-ui`. |
| `--session-metrics <folder>` | Opens the desktop app with a project folder and records low-overhead UI session metrics until the window exits. |
| `--session-metrics-output <file>` | Writes the detailed session metrics JSON report to a specific file. If omitted, DevProjex writes it under the user's local DevProjex session metrics folder. |
| `--export <mode>` | Exports project text and exits without showing the window. Supported modes: `tree`, `content`, `tree-content`. |
| `--copy <mode>` | Exports the effective project tree as physical files and exits without showing the window. Supported modes: `folder`, `zip`. Requires `--output`. |
| `--output <path\|->`, `-o <path\|->` | Sets the text export or project copy destination. `-` is valid only for text export stdout. Folder copy expects a parent directory; ZIP copy expects an archive path. |
| `--export-format ascii\|json\|xml\|md`, `--format ascii\|json\|xml\|md` | Selects tree format for `tree` and `tree-content` exports. Content remains plain text. |
| `--last` | Opens the most recent local project folder in the desktop UI. Cannot be combined with `--path` or a positional folder. |
| `--preview` | Opens preview after the project is loaded in the desktop UI. |
| `--preview-mode tree\|content\|tree-content` | Selects the desktop preview content mode at startup and opens preview. |
| `--tree-format ascii\|json\|xml\|md` | Selects the desktop tree format at startup. This is separate from headless `--format`. |
| `--tree-filter <text>` | Opens the desktop tree filter with the provided query. |
| `--preview-search <text>` | Opens preview and the tree search bar with the provided query. |
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

`--preview-mode` implies `--preview`. `--preview-search` also implies `--preview` because the command is meant to show the project and tree search state immediately.

`--tree-filter` and `--preview-search` cannot be combined. The desktop UI intentionally shows only one tree text tool at a time, so the CLI keeps that same rule instead of silently choosing one.

Desktop startup options require either a project path or `--last`. They are not valid with `--no-ui`, `--silent`, `--export`, or `--copy`.

## Session Metrics

`--session-metrics <folder>` opens the normal desktop app and records one interactive session until the window closes:

- CPU, working set, private memory, managed memory, and GC samples;
- project load timing;
- tree search and tree filter timing, match counts, and cache/fallback hints;
- tree format and preview mode switches;
- copy/export payload sizes;
- scheduled and completed memory cleanup events.

```bash
devprojex --session-metrics "/home/me/projects/app" --preview --tree-format md
devprojex --session-metrics "C:\Projects\App" --session-metrics-output "C:\Reports\devprojex-session.json"
```

The detailed JSON report is written automatically when the window closes. If `--session-metrics-output` is omitted, the report is saved under the user's local DevProjex session metrics folder.

Search and filter text is not stored in the report. DevProjex records only the query length and a salted per-report fingerprint so repeated queries can be correlated inside one report without exposing the query itself.

`--session-metrics` is a desktop UI mode. It can be combined with desktop startup options such as `--preview`, `--preview-mode`, `--tree-format`, `--tree-filter`, and `--preview-search`. It cannot be combined with `--path`, positional folders, `--last`, `--benchmark`, `--report`, `--export`, `--copy`, selection overrides, `--strict`, `--no-ui`, or `--silent`.

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

`--benchmark` is intentionally one fixed scenario: project report analysis. It cannot be combined with `--path`, report/export/copy options, desktop startup options, selection overrides, `--strict`, `--no-ui`, or `--silent`.

## UI Benchmark

`--benchmark-ui <folder>` runs one standard desktop UI benchmark profile:

- cold UI process runs: DevProjex starts itself as child processes with `--session-metrics <folder>` and an internal deterministic UI script;
- each child opens the real Avalonia window, loads the project, opens preview, switches tree formats and preview modes, applies tree search and tree filter, waits for idle, closes the window, and writes a session metrics report;
- stdout prints a short human-readable summary;
- a detailed JSON report aggregates the child session reports.

```bash
devprojex --benchmark-ui "/home/me/projects/app"
devprojex --benchmark-ui "C:\Projects\App" --benchmark-output "C:\Reports\devprojex-ui-benchmark.json"
```

The UI benchmark measures child process wall time, CPU time, process memory, project load timing, scripted preview/search/filter timings, session CPU/memory/GC samples, exit codes, and captured errors. The JSON report also records application/runtime/OS details, the exact child command line, and paths to the raw session metrics reports.

`--benchmark-ui` is intentionally one fixed scenario: standard desktop UI workflow. It cannot be combined with `--benchmark`, `--session-metrics`, `--path`, report/export/copy options, desktop startup options, selection overrides, `--strict`, `--no-ui`, or `--silent`.

## Text Exports

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

## Project Copies

`--copy` writes the current effective project tree as real files. It uses the same root-folder, extension, and ignore selection pipeline as reports and text exports. The source project is never modified.

Create a new `<ProjectName>-copy` directory under a destination parent:

```bash
devprojex "/home/me/projects/app" --copy folder -o ./submissions
```

Create a ZIP archive:

```bash
devprojex "C:\Projects\App" --copy zip -o "C:\Submissions\App-copy.zip"
```

- `folder`: `--output` is the parent directory. Name conflicts are resolved as `<ProjectName>-copy (2)`, `<ProjectName>-copy (3)`, and so on without overwriting existing results.
- `zip`: `--output` is the archive path. DevProjex appends `.zip` when the extension is omitted.
- both modes preserve the effective directory structure, empty directories, binary bytes, Unicode names, and safe file timestamps;
- `--roots`, `--ext`, and `--ignore` restrict the copied effective tree exactly as they do for analysis;
- folder and ZIP writes use adjacent staging output and publish the final result only after success;
- destination paths equal to or inside the source project are rejected, including paths that resolve there through symbolic links or junctions;
- stdout contains exactly one line: the absolute path of the completed folder or ZIP.

`--copy` cannot be combined with reports, `--export`, benchmark modes, session metrics, or desktop startup options. Run each output action as a separate command.

## Output Contract

Automation-friendly output is kept strict:

- `stdout`: help text, version text, generated file paths, benchmark summaries, implicit or explicit JSON report payloads, or text export payloads.
- `stdout` for `--copy`: exactly one absolute path to the completed folder or ZIP archive.
- `stdout` for `--session-metrics`: a short line with the saved JSON report path after the desktop window closes.
- `stderr`: parse errors, invalid command combinations, runtime failures, and cancellation messages.
- no UI is created for `--help`, `--version`, `--no-ui`, `--export`, `--copy`, or `--benchmark`. `--session-metrics` opens one interactive UI session, and `--benchmark-ui` opens real UI child processes for repeatable UI measurement.
- only one stdout payload can be produced by one command. Do not combine `--copy` with reports or text exports, and do not combine stdout text export with report output in the same command.

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
| `1` | Runtime failure, strict-mode diagnostics, unavailable project path, or failed report/export/copy write. |
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

Export the selected effective tree to a new folder:

```bash
devprojex "/home/me/projects/app" --copy folder -o ./submissions --roots src --ext cs --ignore git-ignore
```

Export the effective tree to a ZIP archive:

```powershell
DevProjex "C:\Projects\App" --copy zip -o "C:\Submissions\App-copy.zip"
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
