# MCP process benchmark

This project is intentionally outside `DevProjex.sln`. It starts a built `devprojex.dll`
as an MCP stdio process, warms each operation once, and prints the median, minimum,
maximum, spread, client allocation, and response size for at least five measured calls.

```powershell
dotnet run -c Release --project tools/Benchmarks/Mcp/DevProjex.Benchmarks.Mcp.csproj -- `
  --host Apps/TerminalHost/bin/Release/net10.0/win-x64/devprojex.dll `
  --root . --repetitions 5

dotnet run -c Release --project tools/Benchmarks/Mcp/DevProjex.Benchmarks.Mcp.csproj -- `
  --host Apps/TerminalHost/bin/Release/net10.0/win-x64/devprojex.dll `
  --synthetic-files 20000 --file d000/f00000.ts --seed d000/f00000.ts --repetitions 5
```

Use `--only related_files` (or a comma-separated operation list) for a focused rerun.

Client allocation is measured in the benchmark process. Server-side content-pipeline
counters are asserted by targeted integration tests so the measurement protocol does
not add a diagnostic field to MCP responses.

The declaration lookup microbenchmark uses generated source in memory and performs no server or
network calls. It reports elapsed time, allocations, and the number of declaration records visited:

```powershell
dotnet run -c Release --project tools/Benchmarks/Mcp/DevProjex.Benchmarks.Mcp.csproj -- search-declarations --repetitions 5
```

The live-state retention measurement creates 1, 8, and 50 in-memory roots and reports the
managed bytes retained after their effective plans leave scope:

```powershell
dotnet run -c Release --project tools/Benchmarks/Mcp/DevProjex.Benchmarks.Mcp.csproj -- live-roots --nodes-per-root 10000 --repetitions 5
```

The stored-page attribution measurement compares scanning every path against the recorded line-range
index for packs containing 1,000, 10,000, and 100,000 paths:

```powershell
dotnet run -c Release --project tools/Benchmarks/Mcp/DevProjex.Benchmarks.Mcp.csproj -- pack-attribution --repetitions 5
```
