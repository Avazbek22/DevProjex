# Secrets and content-pipeline benchmarks

Run from the repository root:

```powershell
dotnet run -c Release --project tools/Benchmarks/Secrets
```

The BenchmarkDotNet suite uses its default warm-up and measurement policy with `MemoryDiagnoser`. Detector inputs
cover clean source, high-volume rejected candidates, and accepted findings; the line-index cases
cover LF, CRLF, and mixed line endings. Stop-word cases compare the linear lookup with
`SearchValues` on the actual pinned allowlist sizes. Operational counters for analyze, context export, the
nonmaterializing measure path, and GUI metrics are reported by the focused integration tests that
use `ContentPipelineDiagnostics` and `MetricsPipelineIoTestPoint`.

Compare the three real-process operations against a baseline host with alternating run order:

```powershell
pwsh tools/Benchmarks/Secrets/Measure-Operations.ps1 `
  -BaselineHost ../baseline/Apps/TerminalHost/bin/Release/net10.0/win-x64/devprojex.dll
```

The script performs one unmeasured warm-up per build and operation, then reports at least five
measurements as JSON. It samples peak working set while the process is alive and removes its scratch
outputs when the run finishes. `dotnet run -c Release --project tools/Benchmarks/Secrets -- --verify`
prints deterministic finding counts and hashes for the three detector corpora; replace `--verify`
with `--diagnostics` to print the operation-local work counters.
