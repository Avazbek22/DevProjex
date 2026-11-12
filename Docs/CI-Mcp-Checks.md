# MCP compatibility checks

These checks use the built TerminalHost server over stdio and the official client. They do not
change the catalog, published limits, or server implementation. The baseline is
`af88c2bf47f9481858eda5d435c764183d4d1a1a`.

## Local file transport and environment variables

`RepositoryTransportPolicy` permits local Git transport only inside an internal test-host scope.
Commit `0133df97` removed the environment-controlled grant from shipped builds. The process check
sets both `DEVPROJEX_INTERNAL_TEST_ALLOW_FILE_GIT` and `DEVPROJEX_TEST_HOST_ALLOW_FILE_GIT`, points
`CLAUDE_PROJECT_DIR` outside the registered root, and verifies a readable control file inside it.

`get_tree`, `analyze`, `search_project`, `get_file`, `pack_context`, and `related_files` reject the
outside `file://` project: `DPX-MCP-REMOTE-DISABLED` without remote permission, and
`DPX-MCP-INVALID-ARGUMENTS` with it. An absolute outside file path produces
`DPX-MCP-ROOT-VIOLATION`, and discovery does not list the outside directory. No response contains
its content marker. `LocalFileRemoteOutsideRootsAndQueryCredentialsAreRejectedBeforeRemoteServicesAreCreated`
separately requires zero remote-service creations for rejected URLs.

## Scalar path selection

Commit `10f5b4ec` made a string equivalent to a one-item path list. The process check selects
`src/router` with both shapes, returns its nested files, and excludes unrelated files. Numeric
`paths: 7` still fails with `DPX-MCP-INVALID-ARGUMENTS`. This is not a conversion to glob semantics.

## Looking up a file name

Patterns are root-relative: `Target.cs` does not mean `**/Target.cs`. A missing root-only name
reports the empty selection and names the recursive spelling. `paths` selects existing literal
paths; `search_project` searches content rather than file names. Commit `3ea12a05` added the
parameter guidance. The process checks cover eight natural name-query forms, recursive patterns,
a selected directory, a missing literal path, and an ordinary unsuccessful content search that
must not acquire name-query guidance.

## Connection payload

`RealProcessKeepsTheConnectionPayloadInsideItsBudget` measures the delivered JSON `tools/list`
result of the default reduced set, not a fresh serialization. With one local root the result
stays between 13,270 and 14,020 characters. Three differences are pinned
exactly: a second local root adds 1,820 by publishing `list_projects` and `project`,
`--allow-remote` adds 2,265 by publishing `list_projects`, `project` and `branch`, and
per-call exclusions add 2,115. Instructions remain between 900 and 1,200 characters.
The test prints actual sizes into its result log. These are wire-character limits, not API usage
or tokenizer measurements; no model call is involved.

Measured by the process check on 2026-11-12: one local root 13,770 characters,
two local roots 15,590, one root with `--allow-remote` 16,035, one root
with per-call exclusions 15,885. The full set with one local root measures 27,141,
unchanged since 2026-11-12. The ceiling leaves 250 characters for the next intentional catalog
change.

`RealProcessPublishesTheSelectedToolSetWithinItsOwnBudget` holds each tool set with one local
root under its own ceiling, 250 characters above the measured size: full 27,391, full with
per-call exclusions 30,352, reduced 14,020, reduced with per-call exclusions
16,135. `RealProcessPublishesTheFullToolSetUnchanged` pins the SHA-256 of
the full one-root result, so the full catalog cannot change by accident.

## Repeated service notices

Commits `7394e328`, `1a77ebf4`, and `e7cd75a4` memoize unchanged notices only after delivery and name
only the lines a response withholds. Tests cover a new session, unchanged and changed settings,
empty selection, an error, a stored-result pointer that did not deliver notices, and responses
that previously delivered only some of the lines. Detail-mix lines still appear on every call
that produced a mix: unlike unchanged session notices, they describe that call's contents.

## Results

| Topic | Current status | Process evidence |
|---|---|---|
| Environment-controlled `file://` transport | Already corrected in `0133df97` | Both permission modes reject all six project-reading tools, an outside absolute file, and outside discovery |
| Scalar `paths` | Already corrected in `10f5b4ec` | String and one-item list select the same directory; numeric input is rejected |
| Name lookup through a tree | Already explained in `3ea12a05` | Recursive spelling returns files; root-only and content-query forms give the specific guidance |
| Connection schema budget | Not reproduced within the current wire-character budget | Both modes and instructions satisfy unchanged caps and the exact 2,616-character difference |
| Duplicate unchanged service notices | Already corrected in the memoization commits above | New, unchanged, changed, empty, failed, stored-pointer and partial-delivery responses are checked |

Seventeen selected process cases pass, plus two integration cases that reject URL credentials and
disallowed hosts before creating remote services. All result directories pass the executed-results
guard. The CI-tier contract passes separately; no trigger or permission changed.

## Targeted commands

```powershell
dotnet test Tests/DevProjex.Tests.Terminal/DevProjex.Tests.Terminal.csproj -c Release -m:1 --filter "FullyQualifiedName~RealProcessEnvironmentCannotAdmitAFileUrlOrAnOutsideProject|FullyQualifiedName~RealProcessListsOneDirectoryInASingleCallAndReadsAScalarLikeAOneItemList|FullyQualifiedName~RealProcessListsOneSubdirectoryAndAnswersAFileNameLookup|FullyQualifiedName~RealProcessAnswersEveryNaturalFileNameSearchWithAResultOrTheFormToType|FullyQualifiedName~RealProcessLeavesAContentSearchThatSimplyFoundNothingUnchanged|FullyQualifiedName~RealProcessKeepsTheConnectionPayloadInsideItsBudget|FullyQualifiedName~RealProcessSendsServiceNoticesOnceWhileTheyKeepSayingTheSameThing|FullyQualifiedName~RealProcessRepeatsTheDetailMixOnEveryCallThatProducedOne|FullyQualifiedName~RealProcessRepeatsServiceNoticesWhenTheirContentChanges|FullyQualifiedName~RealProcessStartsEverySessionWithTheFullServiceNotices|FullyQualifiedName~RealProcessKeepsExplainingAnEmptySelectionEveryTime|FullyQualifiedName~RealProcessRepeatsServiceNoticesAfterAResponseThatCouldNotCarryThem|FullyQualifiedName~RealProcessRepeatsServiceNoticesAfterAFailedCall|FullyQualifiedName~RealProcessNeverReportsAnUnchangedProtectionLineInAnAnalyzeOnlySession|FullyQualifiedName~RealProcessNamesOnlyTheProtectionLineWhenTheSessionNeverCarriedFilters|FullyQualifiedName~RealProcessNamesTheLinesEachResponseActuallyWithholds" --logger "trx;LogFileName=mcp-checks.trx" --results-directory TestResults/mcp-checks
dotnet test Tests/DevProjex.Tests.Integration/DevProjex.Tests.Integration.csproj -c Release -m:1 --filter "FullyQualifiedName~LocalFileRemoteOutsideRootsAndQueryCredentialsAreRejectedBeforeRemoteServicesAreCreated" --logger "trx;LogFileName=transport.trx" --results-directory TestResults/transport
```

The commands are sequential. The result guard must accept each nonempty completed result directory:

```powershell
./Scripts/ci/Test-ExecutedTests.ps1 -ResultsPath TestResults/mcp-checks,TestResults/transport -JobName "MCP compatibility"
```

These checks do not exhaust every URI encoding, client transformation, or possible environment
variable. They pin the previously used transport grants, current file-name guidance, accepted
path shapes, delivered catalog size, and service-notice delivery behavior.
