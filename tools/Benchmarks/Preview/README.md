# Preview selection metrics benchmark

Run from the repository root with .NET 10:

```powershell
dotnet build tools/Benchmarks/Preview/PreviewBenchmarks.csproj -c Release -m:1 -p:BuildInParallel=false
dotnet run --project tools/Benchmarks/Preview/PreviewBenchmarks.csproj -c Release --no-build
```

The harness calculates selection metrics for 199,900 of 200,000 source lines. Each line
contains Cyrillic, CJK, a surrogate pair and a CRLF terminator; both selection boundaries
cut through their lines. It exercises in-memory and file-backed documents separately.
Every invocation must preserve the exact line, UTF-16 character and estimated-token counts.

Document construction is excluded. Each storage variant receives one warm-up followed by
seven measured invocations. Output reports the median elapsed time, current-thread managed
allocations and all timings in sorted order. File-backed results describe warm filesystem
cache performance, not cold disk throughput. Allocations do not measure retained or native
memory. Compare baseline and modified revisions on the same machine and configuration.

The file-backed fixture uses a unique temporary file, removed when the document is disposed
and by a final cleanup attempt. The harness does not read or write project source files.

Related correctness tests:

```powershell
dotnet test Tests/DevProjex.Tests.Unit/DevProjex.Tests.Unit.csproj -c Release -m:1 -p:BuildInParallel=false --filter "FullyQualifiedName~PreviewSelectionMetricsCalculatorTests"
```
