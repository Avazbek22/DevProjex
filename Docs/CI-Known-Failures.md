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

Two tests, the same scenario and the same assertion, failing on Windows, macOS and Linux across
branches whose diffs cannot affect them.

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

**Cause.** The server's write order is correct. `McpProgressReporter.CompleteAsync` awaits its
serialized pending notifications, including the forced terminal notification, before the tool
returns. The race is in client-side observation: ModelContextProtocol 2.2.0 yields and dispatches
incoming notifications and responses independently. Its convenience `CallToolAsync` progress
overload disposes the temporary notification subscription when the response completes, before an
already received notification necessarily reaches that subscription. A persistent subscription
avoids that loss but does not make callback completion order equal transport order.

The original assertions treated callback completion order as the server's sequence:

```csharp
Assert.InRange(progress.Values.Count, 2, 20);
Assert.Equal(5f, progress.Values[0].Progress);
Assert.Equal(100f, progress.Values[^1].Progress);
```

Waiting only for the terminal callback did not finish the correction: an earlier callback can
complete afterwards. `DelayedEarlierCallbackDoesNotChangeTheObservedProgressSequence` proves this
with an event barrier, not a delay. The recorded stream contains `5`, `100`, then the tool result;
the endpoint callbacks complete as `100`, `5`. Other notifications may arrive between them and
are independently awaited as well. The old observation fails with `Expected: 5 · Actual: 100`
even though the transport-order assertion passes.

**Closed.** Both large-manifest tests now take counts and ordered values from the recorded
completed call. An explicit token-scoped subscription remains alive until all the recorded
notifications have been delivered; the delivered multiset must equal the recorded one. The
assertions still require 2–20 notifications, first exactly `5`, last exactly `100`, strictly
increasing transport values, and every progress notification before the tool result. Terminal
also rechecks the complete recording at EOF. No retry, skip, value tolerance, throttle or product
timeout changed. Both whole-test limits and both delivery budgets are unchanged.

The server guarantees notification-before-result **on the transport**. A client dispatching
messages concurrently must not infer callback completion from a completed request; it must keep
its subscription alive through delivery. The historical failures above remain recorded.

Sequential local runs on Windows, .NET SDK 10.0.401, one exact test filter per invocation:

| Test | Original observation | Completed-transport observation |
|---|---|---|
| `RealProcessThrottlesDependencyProgressForTenThousandFiles` | 20/20 passed | 20/20 passed |
| `DependencyProgressForTenThousandFilesStaysThrottledAndKeepsEndpoints` | 20/20 passed | 20/20 passed |

The ordinary local sample did not reproduce the historical failure. The event-barrier test did:
it failed before the observation correction and passed after it, independently of machine speed.
The unchanged whole-test limits are 120 seconds for Terminal and 60 seconds for Integration;
their delivery limits remain 30 and 60 seconds respectively.

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

**Current verification.** On Windows with .NET SDK 10.0.401, Microsoft.NET.Test.Sdk 18.9.0,
xUnit v3 3.2.2 and its VSTest adapter 3.1.5, a nonexistent exact filter still exits 0 and writes
`outcome="Completed" total="0" executed="0"`. The result guard rejects it. A real
`Category=TerminalCommand` test with an exact name is discovered and executed; the historical
assembly-info JSON failure was not reproduced by that check. The no-match exit is runner behavior;
accepting it without an executed-results check is our configuration defect, not proof that the
assembly has no tests.

The main `.NET CI` test jobs already had the guard at this baseline. The six filtered test steps
in Release Validation did not. They now write separate TRX directories, preserve native test
failure exits, and invoke the same guard immediately. Their results are uploaded even on failure.

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

**Current verification.** The versions above still produce a successful-looking console summary
when one sibling test passes and another is stopped by `--blame-hang-timeout 3s` with dump type
`none`: `Passed: 1`, `Failed: 0`, followed by the aborted-run notice. The native exit is 1, but the
TRX reports `outcome="Failed"` with `executed="1" passed="1" failed="0"`. The original result
guard accepted that exact TRX because it rejected only `Aborted`, `Error`, and `Timeout`.

**Closed at the gate, not in the external reporter.** The guard accepts only `Completed` or
`Passed`, requires individual outcome totals to equal executed tests, and rejects any failed,
errored, timed-out, aborted or disconnected counter. The reproduced TRX and four added contradictory
summary fixtures are rejected. Native failure exits are checked before the guard: neither a console
word nor completed-looking counters can replace that status.

The isolated Windows reproduction also left the xUnit executable child alive after Blame killed
its test-host parent. That child held an inherited output pipe open while PowerShell collected
`2>&1`; fixture cleanup terminated that exact child before the command's captured output finished.
This is not a product server process. A harness capturing intentionally hanging external tests
needs a bounded process-tree cleanup, and must not interpret the partial summary as completion.

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


## Blocking detector entry before generation invalidation

[Run 34963511614, first attempt](https://github.com/Avazbek22/DevProjex/actions/runs/34963511614/attempts/1),
commit `1faac5c637a31e500bc09ef6ce9a63cff605051a`, CI (Windows / Unit):
`DevProjex.Tests.Unit.SecretRedactionCacheTests.ObsoleteScope_CannotRepopulateCacheOrPublishSnapshot(invalidation: ProjectSwitch)`.
The result records `00:00:05.0100270` and `Assert.True() Failure`, `Expected: True`,
`Actual: False`, at `SecretRedactionCacheTests.cs:line 521`.

That assertion waits for detector entry. It runs before invalidating the generation, so the
failure proves neither obsolete publication nor obsolete cache retention. The old observation
recorded no worker status or exception. The exact historical delay cannot be distinguished
between queued work, file input, and an unobserved early exception from that log. The second
attempt passed this case in `00:00:00.0071284`; that does not establish stability.

The test itself schedules the synchronous detector through `Task.Run` and synchronously waits
for its signal. Its worker therefore depends on spare shared-pool capacity, and an early worker
exception is indistinguishable from missing entry. Release was only on the successful path,
leaving a late-starting detector blocked after an assertion failed. Three deterministic controls
failed with that observation: the worker was pooled, an input exception became an entry timeout,
and an assertion failure did not release the detector.

The blocking fixture now starts on a dedicated worker with the default scheduler. File input is
read before scheduling, and entry is awaited asynchronously together with worker completion.
An early exception propagates directly; a successful worker without entry is rejected; a timeout
includes both task states. A `finally` releases the detector, cancels operation-owned work, and
awaits completion before disposing the fixture. Both tests sharing this detector use that lifecycle.
Cancellation, successful entry, completion without entry, failure before entry, timeout diagnostics,
and cleanup after assertion failure have explicit controls. The five-second entry budget and
all generation/cache/publication assertions are unchanged. No product code, skip or retry changed.

Sequential exact-filter runs on Windows, .NET SDK 10.0.401:

| Case | Before | After |
| --- | ---: | ---: |
| `ObsoleteScope_CannotRepopulateCacheOrPublishSnapshot`, ProjectSwitch | 20/20 | 20/20 |
| Same test, Disable and Reset | 20/20 each | 20/20 each |

These isolated runs did not reproduce the historical stall. The shared-pool assumption and
cleanup/diagnostic defects are corrected; the original machine-level cause remains unconfirmed.
This entry does not declare the failure harmless or establish loaded-runner stability.

## What now fails that did not

`Scripts/ci/Test-ExecutedTests.ps1` runs after the `.NET CI` and Release Validation test steps and
fails the job when a results directory is absent, when no `.trx` was written, when its outcome is
anything other than `Completed` or `Passed`, or when the executed count is zero. Each results directory is judged on its
own: a job that writes two of them has to have run something in each, because one full directory
saying nothing about the other is how a suite that enumerated nothing would pass unnoticed.

It reads the result file rather than the console summary, because the console cannot be trusted for
either failure above: in entry 2 no `Passed!` or `Failed!` line was printed at all, and in entry 3 a
`Passed!` line was printed for a run that had been aborted.

The check is itself checked. `Scripts/ci/Test-ExecutedTestsContract.ps1` runs in `prepare-matrix`
and drives it in both directions against built fixtures: six runs it must accept and fourteen it must
reject. A check that quietly became a no-op fails the fourteen; a check that stopped locating results —
which is what happened once, when a completed run of 433 tests was reported as empty because the
UI suite writes its `.trx` elsewhere — fails the six. There is no state in which the check does
nothing and that script still passes.

The rejecting cases are there because each one was reachable. Every result file is judged on its
own and so is every results directory, because anything that adds up first lets a healthy sibling
vouch for an empty one — which is the shape entry 2 actually had, a suite that enumerated nothing
sitting beside one that had not. Only files written directly into the directory are read, because
recursing found results left underneath it by an earlier run and counted them as this one's. And
counters that contradict themselves are refused: more executed than exist, or an executed count
that differs from the sum of passed, failed, errored, timed-out and aborted tests. A successful
completion label does not excuse unsuccessful counters beneath it.

One limit is worth stating rather than leaving to be discovered. Writers disagree on summary
vocabulary: VSTest can label a crashed host `Aborted`, `Error`, or `Failed`, while the xUnit writer
behind `--report-xunit-trx` uses different labels. The guard accepts only explicit successful
outcomes. A host interrupted before its writer observes the interruption cannot promise that its
artifact describes the interruption; native exit status remains mandatory, including for UI.
The check supplements that status and runs only when the steps before it succeeded.

That UI divergence is fixed at its cause rather than worked around. The UI suite runs on the
Microsoft Testing Platform, so its `--results-directory` is passed after `--` to the test
executable, which resolves a relative path against the test project rather than the repository.
Those two steps now pass an absolute path, so every suite writes where the workflow says and a
missing directory means what it says: the step produced nothing.

The two jobs that ran without a `--logger` — `documentation-contracts` and `ranking-eval-tests` —
now write one, so that they can be checked at all. A stale `--filter` in either of them would
otherwise match nothing and report success, which is the same failure as entry 2 arriving by a
different route.
