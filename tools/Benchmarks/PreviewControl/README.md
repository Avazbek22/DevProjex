# Preview control selection benchmark

Run from the repository root with .NET 10:

```powershell
dotnet build tools/Benchmarks/PreviewControl/PreviewControlBenchmarks.csproj -c Release -m:1 -p:BuildInParallel=false
dotnet run --project tools/Benchmarks/PreviewControl/PreviewControlBenchmarks.csproj -c Release --no-build
```

The harness creates a detached preview control with 100,000 Unicode lines and measures
`GetSelectedText` and the clipboard-copy pipeline separately for in-memory and file-backed
documents. A headless Avalonia platform supplies control services; the harness does not open
a window or change the system clipboard. Reflection invokes
the existing internal copy-provider entry point so the production API stays unchanged.

Every invocation verifies the exact selected text, clipboard line endings and admission
character count. Construction is excluded; one warm-up precedes five measured invocations.
The output includes median elapsed time, current-thread managed allocations and the sorted
timings. Results include payload materialization, use warm filesystem caches and do not
measure native or retained memory. Compare revisions on the same machine and configuration.

The file-backed fixture is disposed after its measurements. Its temporary backing file is
removed by the preview document's normal cleanup.

Related behavior tests cover text fallback, both document storage types, Unicode and partial
surrogate selections, reversed and stale ranges, line-ending compatibility, and cancellation
by the clipboard-admission event:

```powershell
dotnet test Tests/DevProjex.Tests.Unit/DevProjex.Tests.Unit.csproj -c Release -m:1 -p:BuildInParallel=false --filter "FullyQualifiedName~VirtualizedPreviewTextControlSelectionStreamingTests"
```
