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

The scan report keeps `analyze` and `export context` wall time, CPU time, and
peak RSS as separate measurements, then also reports their sum for compatibility
with the historical comparison. It additionally runs two identical narrow
tree/search/read sequences in one MCP server process. Raw samples and medians are
written to the same JSON document; committed measurement inputs live in
[`results`](results).

For stage attribution, collect one unprofiled control run and one trace using the
same command and corpus. The application emits no content through its diagnostic
provider:

```powershell
dotnet-trace collect --providers DevProjex-ContentPipeline,Microsoft-DotNETCore-SampleProfiler,Microsoft-Windows-DotNETRuntime:0x1C000080018:5 -- `
  <devprojex> export context <corpus> --exclude none --git-mode gitignore --hide-secrets --force -o <output>
```

The runtime keyword enables GC/allocation events; `DevProjex-ContentPipeline`
adds selection, read, compression, detector initialization, detection,
redaction/output, cleanup, queue-wait, and byte-budget timing without recording
paths or matched values. Trace files are temporary profiling output and are never
committed.

The cold label means a new process, a fresh worktree path, and a fresh application
cache. The script cannot flush the operating-system page cache without privileged
host changes, so it records that limitation in the JSON result. Repomix is acquired
with `npx --yes repomix@1.17.0`; registry download and resolution happen before the
timed samples. A failed tool pair is discarded and retried once with a clean
application cache; a second failure stops the run and no partial report is written.
