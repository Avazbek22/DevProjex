# Contributing to DevProjex

Thank you for improving DevProjex. Contributions should preserve its core
workflow:

> inspect → select → verify → export

The source project is read-only. GUI, TUI, and CLI follow the same product
workflow and source-safety contract, but they are not one interchangeable
implementation pipeline. Validate every affected surface when changing shared
behavior.

By participating, you agree to follow [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).
Usage questions belong in [GitHub Discussions](https://github.com/Avazbek22/DevProjex/discussions);
security reports follow [SECURITY.md](SECURITY.md).

## Prerequisites

- the .NET SDK selected by `global.json` (currently .NET 10);
- Git;
- PowerShell 7 for release scripts;
- a supported Windows, Linux, or macOS host.

Some PTY tests require:

- Windows: ConPTY on a supported Windows 10/11 host;
- Linux/macOS: a native host with `forkpty`;
- an interactive terminal for manual TUI validation;
- `tmux` or `zellij` only for their respective compatibility checks.

Cross-publishing proves that an artifact can be produced, not that it runs
natively on that operating system or architecture.

## Build and run

From the repository root:

```bash
dotnet restore DevProjex.sln
dotnet build DevProjex.sln -c Release --no-restore
dotnet run --project Apps/Avalonia/DevProjex.Avalonia.csproj
```

Run a direct terminal command without initializing the desktop UI:

```bash
dotnet run --project Apps/Avalonia/DevProjex.Avalonia.csproj -- analyze . --plain
```

The normative public syntax is in
[Docs/CLI-V1-Contract.md](Docs/CLI-V1-Contract.md). Do not add a command,
option, choice token, alias, or machine field without updating the command
tree, help, completion, documentation, and public-boundary tests together.

## Tests

Build first, then run the solution:

```bash
dotnet test DevProjex.sln -c Release --no-build
```

Run terminal-specific tests:

```bash
dotnet test Tests/DevProjex.Tests.Terminal/DevProjex.Tests.Terminal.csproj -c Release --no-build
```

Target a category or scenario while iterating, but run the complete relevant
project before requesting review:

```bash
dotnet test Tests/DevProjex.Tests.Unit/DevProjex.Tests.Unit.csproj -c Release --no-build --filter "Category=TerminalCommand"
dotnet test Tests/DevProjex.Tests.Integration/DevProjex.Tests.Integration.csproj -c Release --no-build --filter "Category=TerminalCommand"
dotnet test Tests/DevProjex.Tests.Terminal/DevProjex.Tests.Terminal.csproj -c Release --no-build --filter "FullyQualifiedName~TerminalPty"
```

Published-binary and PTY tests may use:

- `DEVPROJEX_TUI_TEST_BINARY`: absolute path to a native published executable
  or official launcher;
- `DEVPROJEX_TUI_TEST_REPOSITORY_ROOT`: repository root used by PTY fixtures.

Progress snapshots automatically build the separate
`DevProjex.Tests.Terminal.ProgressHost` test executable. Its checkpoint
variables are test protocol details and must never be added to the published
application.

Do not add a platform skip without documenting the unavailable API or
environment. Report passed, failed, and skipped counts separately.

## Regression-test expectations

For a user-visible defect:

1. reproduce the original behavior and record command, cwd, stdout, stderr,
   exit code, and filesystem effects;
2. add the smallest regression test at the responsible layer;
3. add at least one public-boundary test for parser, process, published binary,
   or PTY behavior;
4. fix the root cause;
5. rerun the original scenario and the full relevant test project.

Tests must not weaken destination safety, symlink/junction handling, secret
redaction, or the source read-only guarantee.

## Snapshot changes

Do not update a snapshot only because a test failed.

1. render or capture the proposed snapshot;
2. inspect its complete diff at supported compact and wide sizes;
3. verify focus, clipping, Unicode cell width, monochrome/plain behavior, and
   terminal restoration;
4. commit the reviewed snapshot together with the behavior change and test.

The pull request should explain intentional visual differences. Unrelated
snapshot churn should be reverted.

## Localization

All user-facing strings go through the existing JSON localization catalog.
When adding a key:

1. add it to every supported locale;
2. preserve format placeholders and their order;
3. test at least English and Russian;
4. check narrow help/TUI layouts with long translations;
5. ensure machine schema names, enum tokens, and diagnostic codes remain
   stable English identifiers.

Never ship `[[missing.key]]` or hardcoded English in a user-facing CLI/TUI
surface.

## CLI and terminal review

Check at minimum:

- command and option names remain case-sensitive;
- documented choice values accept mixed case but normalize once at parsing;
- `--` ends option and legacy detection;
- stdout contains only the requested payload or real result path;
- operational status, warnings, diagnostics, and errors use stderr;
- redirected JSON/XML parses and contains no ANSI;
- `--plain`, `NO_COLOR`, `TERM=dumb`, and redirected-stream behavior;
- existing/unsafe destinations, dry-run effects, cancellation, and cleanup;
- keyboard-only TUI use at 80×24;
- cursor, color, mouse tracking, and screen buffer restoration after success,
  cancellation, and failure.

## Maintainer-only diagnostics

The hidden `dev` namespace reuses production workflows and is not part of the
stable public CLI:

```bash
devprojex dev benchmark analysis .
devprojex dev benchmark ui .
devprojex dev session . --scenario standard
devprojex dev session . --scenario preview-search-retention
devprojex dev session . --scenario project-memory-lifecycle
```

Do not create a benchmark-only scanner, context plan, or export pipeline.

## Release validation

Before release-oriented changes:

```bash
pwsh ./Scripts/release-all.ps1 -ValidateConfigOnly
```

Review `.github/workflows/release-validate.yml` for the native RID matrix and
published smoke contract. Manual packaging instructions must use the same
publish recipe as the workflow. A local cross-publish is not a substitute for
native CLI, GUI, launcher, and PTY execution.

Before requesting review:

```bash
git diff --check
```

Confirm that no temporary projects, publish output, logs, secrets, IDE files,
or generated snapshots were added unintentionally.

## Contribution license

By submitting a contribution, you agree that it is licensed under the
project's [Apache License 2.0](LICENSE).
