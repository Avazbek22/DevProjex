# Contributing to DevProjex

Thanks for your interest in improving DevProjex! This guide is intentionally lightweight.

## Build and run

From the repository root:

```bash
dotnet restore

dotnet build

dotnet run --project Apps/Avalonia/DevProjex.Avalonia.csproj
```

## Run tests

```bash
dotnet test
```

## CLI diagnostics

The public command hierarchy is documented in
[Docs/CommandLine.md](Docs/CommandLine.md). Maintainer-only performance and
session commands are intentionally hidden from normal root help:

```bash
devprojex dev benchmark analysis .
devprojex dev benchmark ui .
devprojex dev session . --scenario standard
devprojex dev session . --scenario preview-search-retention
devprojex dev session . --scenario project-memory-lifecycle
```

Use `-o` to choose the JSON report path. These commands reuse the production
analysis and desktop workflows; do not add a separate benchmark-only scanner.

## What contributions are welcome

* Bug fixes
* Documentation improvements (README, screenshots, store descriptions)
* Performance improvements
* Tests (unit and integration)
* UI/UX improvements
* Icon mappings
* Localization

## Contribution license

By submitting a contribution, you agree that it is licensed under the current project license (GPL-3.0).
