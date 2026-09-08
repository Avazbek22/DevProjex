# CI policy: three tiers, one rule

Checks are tiered by how close a commit is to a release. A feature push must be fast, a merge
into a version branch must prove the product still builds and ships, and master must prove
every channel from one commit. Every workflow header names its tier; this file is the policy
they point to. The contract test
`Tests/DevProjex.Tests.Terminal/DocumentationAndPackagingContractTests.cs`
(`HeadlessWorkflowsCoverMasterCoreChangesAndReleaseCandidatePinsOneSha`) pins the rules below.
Change the policy and the test together; never delete an assertion to make a change pass.

| Tier | Event | What runs | Budget |
|---|---|---|---|
| 1 Feature | `pull_request` to any branch except master | `.NET CI` and `Release Validation`, both filtered by the change planner; `Grammar Delivery` only for grammar paths; packaging workflows only when their own packaging inputs change | about 31 checks, 13 to 17 minutes |
| 2 Merge | `push` to `v*` | tier 1 unfiltered by event, plus headless archives, container and package dry-runs, AppImage | about 50 checks, once per merge |
| 3 Release | `pull_request` to master, `push` to master, `release: published`, `workflow_dispatch` of `release-candidate.yml` | tier 2 plus Store smoke; the release candidate runs every read-only gate on one SHA with `force_full` | before the tag |

The merge tier attaches its check runs to the commit itself, so the open `v5.x -> master`
pull request shows them without running anything twice.

## Rules that keep this cheap

1. Packaging workflows (`package-headless.yml`, `publish-container.yml`, `publish-packages.yml`,
   `package-appimage.yml`) run on feature pull requests only when their own packaging inputs
   change: that is what their `pull_request.paths` list. Core code paths (`Application/**`,
   `Kernel/**`, `Apps/**`, `Infrastructure/**`) belong to the `push` trigger. Copying the push
   paths into `pull_request` doubles every feature push by roughly 120 machine minutes and
   30 minutes of wall time; that is exactly what happened in September 2026 and why this file exists.
2. Packaging and grammar workflows keep `pull_request: branches-ignore: [master]`. A merge into
   `v5.x` is also a synchronize of the open release pull request; the push run already attached
   its checks to the same SHA, and a pull-request-triggered run on the release PR would repeat
   them. Never replace this with `branches: [master]`, and never remove it.
3. `release-candidate.yml` is `workflow_dispatch` only. It calls every reusable gate, so any
   `pull_request` or `push` trigger on it doubles the whole matrix (this was also tried and
   reverted). Composition changes are covered by the `actionlint` job in `dotnet.yml`.
4. Reusable `*-build.yml` workflows have `workflow_call` only, `contents: read`, and no write
   permission. GitHub validates nested job permissions against the caller regardless of `if:`
   guards, so uploads, registry pushes, and OIDC exchanges live only in the outer publishing
   workflows.
5. Every workflow that runs on `pull_request` or `push` has a `concurrency` group keyed by the
   pull request number or the ref, with `cancel-in-progress` for those two events only. Release
   and dispatch runs are never cancelled.
6. Planner skips (`Scripts/ci/Select-CiPlan.ps1`) belong to `pull_request` and `push`. The
   `force_full: true` inputs are for the release candidate only; a gate that runs under
   `force_full` fails when any of its jobs is skipped.
7. Runner images are chosen on purpose: `ubuntu-22.04` in `appimage-build.yml` is the glibc floor
   for AppImage portability (GitHub deprecates it from 2026-09-17 and retires it on 2027-04-17,
   move to `container: ubuntu:22.04` on `ubuntu-latest` before then); `macos-15-intel` in
   `grammar-delivery.yml` is the last x86_64 image (retires autumn 2027);
   `windows-2025-vs2026` carries the Store toolchain. actionlint does not know these newer
   hosted labels; they are listed in `.github/actionlint.yaml`, keep that list in sync.

## Where a new check belongs

Pick the cheapest tier that can prove the property. In order:

1. A test for product code goes into an existing suite (Unit, Integration, Terminal, UI). The
   change planner routes it to the right matrix cells; no workflow edit is needed.
2. A documentation or contract check goes into the `documentation-contracts` job of `dotnet.yml`
   (Linux, about one minute). The planner runs it when `Docs/**` or the contract sources change.
3. A new artifact or channel gets a read-only `<name>-build.yml` (`workflow_call`, `contents:
   read`), a publishing wrapper with `push: [master, 'v*']` and `release: published` triggers,
   one job in `release-candidate.yml` plus one row in its report table, and one entry in the
   contract test.
4. A smoke that needs a special runner (Store, Intel macOS, arm64) is tier 3 only: `push` to
   master and the release candidate, never tier 1.
5. Timing and memory measurements go to `tools/ScanBenchmark` and `Docs/Benchmarks.md`. A CI
   assertion on wall-clock time is a flake generator, not a check.
