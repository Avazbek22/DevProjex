# Dependency and ranking benchmarks

This project stays outside `DevProjex.sln`. It measures local algorithms with BenchmarkDotNet and
whole dependency/ranking operations with a fixed manifest. Run it from the repository root with one
MSBuild process:

```powershell
dotnet run -c Release --project tools/Benchmarks/Dependencies/DependencyBenchmarks.csproj -- --filter "*"
dotnet run -c Release --project tools/Benchmarks/Dependencies/DependencyBenchmarks.csproj -- operations `
  --root C:\path\to\pinned\corpus --output C:\temp\dependency-operations.json --repetitions 5
dotnet run -c Release --project tools/Benchmarks/Dependencies/DependencyBenchmarks.csproj -- operations `
  --root C:\temp\dependency-synthetic --output C:\temp\dependency-synthetic.json `
  --repetitions 5 --synthetic-count 20000
dotnet run -c Release --project tools/Benchmarks/Dependencies/DependencyBenchmarks.csproj -- fact-budget `
  --repetitions 3
```

The operation runner reports each sample plus median, minimum, and maximum elapsed time and allocated
bytes. Compare builds against the same corpus and alternate their order; filesystem cache and machine
load otherwise dominate the small-corpus timings. `PeakWorkingSetBytes` is process-wide and monotonic,
so use it only as a within-process upper bound, not as an allocation measurement.

The fact-budget matrix uses equal-density virtual manifests at 1,000, 10,000, and 100,000 files. It
reports total allocation and retained-size estimates after fact admission, resolver-context creation,
and resolution for both an unbounded comparison and the bounded pipeline.
