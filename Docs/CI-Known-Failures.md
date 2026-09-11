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

None of those branches touches progress reporting; `b62a612` and `e056940` are merges into `v5.2`.
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

## What now fails that did not

`Scripts/ci/Test-ExecutedTests.ps1` runs after each test step and fails the job when the results
directory is absent, when no `.trx` was written, when a `.trx` reports an outcome of `Aborted`,
`Error` or `Timeout`, or when the executed count is zero.

It reads the result file rather than the console summary, because the console cannot be trusted for
either failure above: in entry 2 no `Passed!` or `Failed!` line was printed at all, and in entry 3 a
`Passed!` line was printed for a run that had been aborted.

The two jobs that ran without a `--logger` — `documentation-contracts` and `ranking-eval-tests` —
now write one, so that they can be checked at all. A stale `--filter` in either of them would
otherwise match nothing and report success, which is the same failure as entry 2 arriving by a
different route.
