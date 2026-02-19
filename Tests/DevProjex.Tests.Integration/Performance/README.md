# Local Performance Tests

These tests are **local-only** and are skipped unless:

- `DEVPROJEX_RUN_LOCAL_PERF=1`

Optional switches:

- `DEVPROJEX_PERF_UPDATE_BASELINE=1` updates baseline values.
- `DEVPROJEX_PERF_BASELINE_PATH=<path>` overrides baseline file location.
- `DEVPROJEX_PERF_ENFORCE_BASELINE=1` makes baseline regressions test-failing.

## Quick Start

```powershell
.\Scripts\perf-local.ps1
```

Update baseline:

```powershell
.\Scripts\perf-local.ps1 -UpdateBaseline
```

## What is measured

- File system scanner operations.
- Tree builder operations.
- Tree/content export pipeline operations.

Each scenario reports:

- median latency
- median allocations
- acceleration/regression vs baseline

By default, baseline regressions are non-blocking (`[regression-detected-nonblocking]`).
Absolute guards for latency/memory are always blocking.
