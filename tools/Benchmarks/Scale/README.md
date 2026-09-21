# Scale microbenchmarks

This project stays outside `DevProjex.sln` and uses generated in-memory fixtures. It performs no
network operations. Run the wide-tree realization matrix with:

```powershell
dotnet run -c Release --project tools/Benchmarks/Scale/DevProjex.Benchmarks.Scale.csproj -- tree-realization --repetitions 5
```

The matrix covers 1,000, 10,000, and 100,000 direct children with zero, one, and fifty percent of
the previous view-model instances preserved. It reports elapsed time, current-thread allocations,
and the comparison counts implied by the linear and indexed merge algorithms.

Run 1,000 repeated profile lookups against 10 KiB, 1 MiB, and near-4 MiB generated stores with:

```powershell
dotnet run -c Release --project tools/Benchmarks/Scale/DevProjex.Benchmarks.Scale.csproj -- profile-reads --repetitions 5
```

This reports elapsed-time spread and the number of full JSON document parses. Fixtures are created
under the system temporary directory and removed after each size.
