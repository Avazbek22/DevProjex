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
