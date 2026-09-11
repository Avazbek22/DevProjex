# Known CI failures

Failures CI has produced where the change under test was not the cause. Each entry names the run,
the commit, the test, and the message as the log printed it, so that the next person to meet one
recognises it instead of rediscovering it.

This list makes such failures visible; it does not excuse them. The rerun policy is unchanged and
nothing here may be rerun away: a red job stays red until someone looks at it, and an entry below is
the record that someone did.

## How to add an entry

Give the run URL, the commit the run was on, the fully qualified test, and the message verbatim.
Say what the change under test touched, because the point of an entry is that the two have nothing
to do with each other. If the cause is understood, name it; if it is not, say so rather than guess.

## Tests outside the blocking layer

None. No test has been moved out of the blocking layer as a result of anything on this list, and no
test is skipped, quarantined, or retried because of it. If one ever is, it belongs in this section
with the reason, so that the cost of the move is recorded beside the failure that prompted it.

---

## 1. Progress notifications read before they arrive

Two tests, the same scenario and the same assertion, failing on Windows and macOS across branches
whose diffs cannot affect them.

- `DevProjex.Tests.Terminal.McpServerProcessTests.RealProcessThrottlesDependencyProgressForTenThousandFiles`
  — `Tests/DevProjex.Tests.Terminal/McpServerProcessTests.BatchReads.cs:99`, failing assertion at line 118
- `DevProjex.Tests.Integration.McpServerIntegrationTests.DependencyProgressForTenThousandFilesStaysThrottledAndKeepsEndpoints`
  — `Tests/DevProjex.Tests.Integration/McpServerIntegrationTests.cs:6078`

| Run | Commit | Branch | Job | Test | Message |
|---|---|---|---|---|---|
| [34364269973](https://github.com/Avazbek22/DevProjex/actions/runs/34364269973) | `b62a612` | `v5.2` | CI (Windows / Terminal) | `RealProcessThrottles…` | `Assert.Equal() Failure: Values differ` · `Expected: 100` · `Actual: 10.0150003` |
| [34357903596](https://github.com/Avazbek22/DevProjex/actions/runs/34357903596) | `8ff592c` | `fix/macos-https-git-fixture` | CI (Windows / Integration) | `DependencyProgress…` | `Expected: 100` · `Actual: 10.0065002` |
| [34361399181](https://github.com/Avazbek22/DevProjex/actions/runs/34361399181) | `fe69ba2` | `fix/mcp-remote-trailer-and-analyze-metrics` | CI (Windows / Integration) | `DependencyProgress…` | `Expected: 100` · `Actual: 42.1534996` |
| [34361399181](https://github.com/Avazbek22/DevProjex/actions/runs/34361399181) | `fe69ba2` | `fix/mcp-remote-trailer-and-analyze-metrics` | CI (macOS / Integration) | `DependencyProgress…` | `Assert.InRange() Failure` · `Range: (2 - 20)` · `Actual: 1` |
| [34364269973](https://github.com/Avazbek22/DevProjex/actions/runs/34364269973) | `b62a612` | `v5.2` | CI (macOS / Integration) | `DependencyProgress…` | `Expected: 100` · `Actual: 10.0065002` |
| [34370676670](https://github.com/Avazbek22/DevProjex/actions/runs/34370676670) | `e056940` | `v5.2` | CI (Windows / Integration), also Linux and macOS | `DependencyProgress…` | `Test execution timed out after 60000 milliseconds` |
| [34586921966](https://github.com/Avazbek22/DevProjex/actions/runs/34586921966) | `596def1` | `chore/ci-known-failures` | CI (Windows / Terminal) | `RealProcessThrottles…` | `Expected: 100` · `Actual: 10.0065002` |

None of those branches touches progress reporting; `b62a612` and `e056940` are merges into `v5.2`,
and `596def1` changes only CI scripts and this document — the clearest evidence available that the
change under test cannot be the cause. That occurrence also puts `10.0065002` on the Terminal test,
a value previously seen only on its Integration sibling, which is what one would expect if the two
share a mechanism rather than a behaviour.
Both tests pass locally, the Terminal one in about nine seconds.

**Cause.** One test producing several different final values is the signature of a delivery race,
and the code says why. The test passes an `IProgress<T>` into `CallToolAsync` and reads
`progress.Values` the instant the call returns:

```csharp
Assert.InRange(progress.Values.Count, 2, 20);
Assert.Equal(5f, progress.Values[0].Progress);
Assert.Equal(100f, progress.Values[^1].Progress);
```

A progress notification is a separate JSON-RPC message from the tool result, and nothing sequences
the last notification before the result is observed. So the final `100` can still be in flight, and
whatever the throttle last emitted stands in its place — `10.0065002`, `10.0150003`, `42.1534996`,
all mid-scan values. When almost nothing has been dispatched yet, the count assertion fails instead.
There is no poll and no wait: `[Fact(Timeout = 120_000)]` bounds the whole test, not the arrival of
a notification. The test carries no `Category` trait, so no CI filter excludes it.

**Not fixed here.** This list is a record; changing the test is a separate change. Whoever takes it
will want the test to wait for the terminal notification rather than assume it has arrived.

## 2. The runner enumerating zero tests

| | |
|---|---|
| Run | [34544434796](https://github.com/Avazbek22/DevProjex/actions/runs/34544434796), job `103093987349` |
| Commit | `5852c86` on `v5.2` |
| Job | Terminal Command Matrix (Windows), step `Run terminal command unit tests` |
| Assembly | `DevProjex.Tests.Unit.dll`, filter `Category=TerminalCommand` |

```
[xUnit.net 00:00:04.25] DevProjex.Tests.Unit: Catastrophic failure: System.InvalidOperationException: Test process did not return valid JSON (non-object). Output:
{"arch-os":"X64", … ,"test-framework":"xUnit.net v3 3.2.2+728c1dce01"}
Waiting 10 seconds for foreground threads to exit...
   at Xunit.v3.TestProcessLauncherAdapter.GetAssemblyInfo(ITestProcessLauncher launcher, XunitProjectAssembly projectAssembly)
No test matches the given testcase filter `Category=TerminalCommand` in …\DevProjex.Tests.Unit.dll
```

```xml
<ResultSummary outcome="Failed">
  <Counters total="0" executed="0" passed="0" failed="0" … />
```

During assembly-info enumeration the child process printed a waiting line after its JSON, the
adapter could not parse the stream, and discovery yielded nothing. The run then reported that
nothing matched the filter — true of what it enumerated, false of the assembly.

**In this occurrence the job did fail**, exiting 1, so it did not look green. What makes the class
dangerous is that nothing guaranteed that: `dotnet test` returns zero when no test matched, the
diagnostic dump runs only `if: failure()`, and the results upload carries `if-no-files-found:
ignore`, so the missing `.trx` that is the exact signature of a failed enumeration was accepted
silently. Reproduced locally by pointing a filter at a class that does not exist: `dotnet test`
exits **0** and prints a `.trx` reading `total="0" executed="0"`.

## 3. A completed-looking summary for an aborted run

| Run | Commit | Job | Test |
|---|---|---|---|
| [34320822644](https://github.com/Avazbek22/DevProjex/actions/runs/34320822644) | `d7eba1a` on `fix/secret-detection-core` | CI (Windows / Terminal) | `RealProcessRelatedFilesReportsBothDirectionsAmbiguityCoverageAndProgress` |
| [34301395749](https://github.com/Avazbek22/DevProjex/actions/runs/34301395749) | — | CI (Windows / Terminal) | same |

```
The active test run was aborted. Reason: Test host process crashed
Data collector 'Blame' message: The specified inactivity time of 8 minutes has elapsed. …
Test Run Aborted.
Passed!  - Failed:     0, Passed:  1471, Skipped:    37, Total:  1508, Duration: 2 m 2 s
```

A literal `Passed!  - Failed:     0` line for a run the host crashed out of. The job failed on the
exit code, but any check that read that line would have called it green. The crashing test is a
close neighbour of the two in entry 1.

## 4. A refreshed tree observed before it was published, macOS

- **Test**: `DevProjex.Tests.UI.MainWindowApplySettingsSelectionUiTests.RefreshProject_PreservesSelectionAndExpansion`
  (`Tests/DevProjex.Tests.UI/MainWindowApplySettingsSelectionUiTests.cs`, failing assertion at line 305)
- **Run**: [34589280620](https://github.com/Avazbek22/DevProjex/actions/runs/34589280620), job `103230753677`
- **Commit**: `c50df55d` on `chore/ci-known-failures`
- **Job**: `CI (macOS / UI)` — `Failed: 1, Passed: 432, Total: 433`
- **Message**: `Assert.NotSame() Failure: Values are the same instance`

The change under test is three files: this document and two PowerShell scripts, one of which runs
after `dotnet test` and only reads the result file it emitted. Fifteen minutes earlier the same
product tree passed the same leg — run
[34588080388](https://github.com/Avazbek22/DevProjex/actions/runs/34588080388) on `3fb4e4f2`, 433 of
433, this test in 0.576 s against 0.595 s when it failed. `acbfa6bb`, the base merged into that
branch, passes all three UI legs in run
[34584479932](https://github.com/Avazbek22/DevProjex/actions/runs/34584479932), and no commit
between `61b5f004` and `acbfa6bb` touches `Apps/Avalonia` at all.

Across 300 runs of `.NET CI` from 2026-08-31 to 2026-09-11 this test failed once — this job. It has
never failed on Linux or Windows. Seventeen other UI jobs failed in that window, all on other tests.

**Cause, as far as the code shows.** The assertion requires that a refresh rebuilt and republished
the tree, so that carried-over state sits on fresh nodes rather than surviving by identity. The two
are the same instance whenever the refresh has not published yet. `MainWindow.ReloadProjectAsync`
returns whether it published, and the test never observes that: it waits on the selection
coordinator, polls a busy flag, then pumps a few settled frames. Any window in which the status
operation has completed — or has not yet begun, the handler being `async void` — while the node
graph has not yet been swapped satisfies that wait early. CI compresses the margin further:
`DEVPROJEX_FAST_UI_TESTS=1` is set at workflow level, which collapses the poll and frame delays to
a millisecond and scales the settle down to roughly two frames, which is why this reproduces on a
runner and not locally.

Not fixed here, and not by this work: the refresh path and its test belong to the desktop surface.
Whoever takes it will want the test to observe what `ReloadProjectAsync` returns rather than infer
publication from a busy flag.


## 5. Git settings reported unavailable after one failed read

- **Cause**: #377 — a failed read of a repository's git configuration was cached and served for five
  seconds without anything looking again, so a single miss took the repository out of reach for
  every caller in that window.
- **Run**: [34584479932](https://github.com/Avazbek22/DevProjex/actions/runs/34584479932), job `103215930387`
- **Commit**: `acbfa6bb` on `v5.2`
- **Job**: `CI (Windows / Terminal)` — `Failed: 1, Passed: 1705, Skipped: 201, Total: 1907`
- **Test**: `DevProjex.Tests.Terminal.GitModeCommandContractTests.DesktopOpenReadinessAcceptsValidMomentaryScopeWithoutGitIgnore(mode: Changes)`
  (`Tests/DevProjex.Tests.Terminal/GitModeCommandContractTests.cs:532`, assertion at `:551`)
- **Message**:

```
Assert.DoesNotContain() Failure: Filter matched in collection
Collection: [ContextDiagnostic { Code = DPX-GIT-STATE-UNAVAILABLE, Severity = Error,
Message = Git path comparison settings could not be resolved., … }]
```

The same job skipped seven tests with `Git is required for this regression test.` between the xUnit
clock readings `00:00:58` and `00:02:00`, and the failure above lands at `00:01:05`, inside that
window. So `git` could not be launched on that runner for about a minute; the read failed, the
failure was cached, and a caller met it.

**Named in #377, not reproduced here.** The issue cites two tests —
`DevProjex.Tests.Integration.McpServerIntegrationTests.RemoteProjectClonesSelectsBranchReusesPinnedCacheAndKeepsJailAndRedaction`
(`Tests/DevProjex.Tests.Integration/McpServerIntegrationTests.cs:2629`) and a Windows terminal test
on a cached remote workspace, which matches
`DevProjex.Tests.Terminal.TerminalWorkspaceContractTests.CachedRemoteWorkspaceHydratesShallowDiffWithoutChangingItsCheckout`
(`Tests/DevProjex.Tests.Terminal/TerminalWorkspaceContractTests.cs:1487`). Both are recorded here
with #377 as their cause, as the issue asks. Neither was found failing: all 60 runs of `.NET CI`
from 2026-09-09 to 2026-09-11 were searched, covering every run of the day the issue describes, and
in them the first is `Passed` where it appears and the second is `Passed` or `NotExecuted` — it is
one of the seven skipped in the window above. The mechanism is confirmed by the code and by the run
recorded here; the two specific occurrences are not in the logs that remain.

**Fixed.** The read is attempted twice before an unreadable answer is believed, so a single miss
costs one extra read rather than five seconds of unavailability, and a repository that genuinely
cannot be read still backs off after the retry instead of being probed by every caller. These
entries come out of this list once the change has run clean for a week.


## What now fails that did not

`Scripts/ci/Test-ExecutedTests.ps1` runs after each test step and fails the job when a results
directory is absent, when no `.trx` was written, when a `.trx` reports an outcome of `Aborted`,
`Error` or `Timeout`, or when the executed count is zero. Each results directory is judged on its
own: a job that writes two of them has to have run something in each, because one full directory
saying nothing about the other is how a suite that enumerated nothing would pass unnoticed.

It reads the result file rather than the console summary, because the console cannot be trusted for
either failure above: in entry 2 no `Passed!` or `Failed!` line was printed at all, and in entry 3 a
`Passed!` line was printed for a run that had been aborted.

The check is itself checked. `Scripts/ci/Test-ExecutedTestsContract.ps1` runs in `prepare-matrix`
and drives it in both directions against built fixtures: five runs it must accept and ten it must
reject. A check that quietly became a no-op fails the ten; a check that stopped finding results —
which is what happened once, when a completed run of 433 tests was reported as empty because the
UI suite writes its `.trx` elsewhere — fails the five. There is no state in which the check does
nothing and that script still passes.

The rejecting cases are there because each one was reachable. Every result file is judged on its
own and so is every results directory, because anything that adds up first lets a healthy sibling
vouch for an empty one — which is the shape entry 2 actually had, a suite that enumerated nothing
sitting beside one that had not. Only files written directly into the directory are read, because
recursing found results left underneath it by an earlier run and counted them as this one's. And
counters that contradict themselves are refused: more executed than exist, or tests counted as
executed while none passed, failed, errored, timed out or aborted.

One limit is worth stating rather than leaving to be discovered. The `outcome` attribute is how a
completed run admits it was cut short, and the two writers disagree on the vocabulary: the VSTest
logger emits `Aborted` and `Error`, while the xUnit writer behind `--report-xunit-trx` emits
neither, so only `Timeout` of the three is reachable for the UI suite. There an aborted host is
caught by the step's own exit code instead, which is why the check runs only when the steps before
it succeeded.

That UI divergence is fixed at its cause rather than worked around. The UI suite runs on the
Microsoft Testing Platform, so its `--results-directory` is passed after `--` to the test
executable, which resolves a relative path against the test project rather than the repository.
Those two steps now pass an absolute path, so every suite writes where the workflow says and a
missing directory means what it says: the step produced nothing.

The two jobs that ran without a `--logger` — `documentation-contracts` and `ranking-eval-tests` —
now write one, so that they can be checked at all. A stale `--filter` in either of them would
otherwise match nothing and report success, which is the same failure as entry 2 arriving by a
different route.
