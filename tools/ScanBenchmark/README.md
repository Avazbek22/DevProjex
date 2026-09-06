# Scan benchmark harness

This directory is intentionally outside the solution. It measures pinned repository
snapshots with the Release headless CLI and the exact npm release of Repomix used by
the report. Corpora and all generated context files live under a uniquely named
system-temporary directory and are removed in `finally` unless `-KeepWorkspace` is
explicitly supplied.

```powershell
dotnet build Apps/TerminalHost/DevProjex.TerminalHost.csproj -c Release
pwsh tools/ScanBenchmark/run-scan-benchmark.ps1 -Repetitions 3
npm install --prefix tools/ScanBenchmark --ignore-scripts
node tools/ScanBenchmark/measure-mcp.mjs <devprojex> <flask-root> <result.json>
```

The cold label means a new process, a fresh worktree path, and a fresh application
cache. The script cannot flush the operating-system page cache without privileged
host changes, so it records that limitation in the JSON result. Repomix is acquired
with `npx --yes repomix@1.17.0`; registry download and resolution happen before the
timed samples. A failed tool pair is discarded and retried once with a clean
application cache; a second failure stops the run and no partial report is written.
