# Importance-aware context ranking

Status: experimental in v5.2. The `importance-v1` weights are selected by the protocol below, dated 2026-09-07, over three pinned repositories. Ranking estimates useful admission order; it does not claim to measure model attention or correctness.

## Frozen evaluation registry

This registry was written before the first ranking run. A required set means that every listed file must be admitted for `AllRequired@B`; an alternative is independently sufficient. The rationale is based on reading call sites and contracts, not on a generated ranking or on files touched by a fix.

Pinned inputs:

| Repository | Commit |
|---|---|
| DevProjex | `c249c30944b15771f351ff2057f77817b1706ea4` |
| yamadashy/repomix | `85e3969b010c72b905203812d1a3f5beb84a2102` |
| pallets/flask | `d318b683471101618febed18996405ad26462110` |

### DevProjex tasks

| Task | Required files | Alternative sufficient set | Why these files are required |
|---|---|---|---|
| Trace a token-budget skip from transformed character cost to its user-facing report. | `Application/Context/ProjectContextDocumentService.cs`; `Application/Context/ProjectContextTokenBudget.cs`; `Apps/Terminal/Execution/TokenBudgetOutput.cs` | `Application/Context/ProjectContextDocumentService.cs`; `Application/Context/ProjectContextTokenBudget.cs`; `Apps/Mcp/DevProjexMcpTools.cs` | The writer supplies transformed character counts, the accumulator makes the greedy decision, and one surface formats the result. |
| Verify that mandatory secret redaction survives a change in selection exclusions. | `Application/Context/ProjectSelectionSpec.cs`; `Application/Secrets/SecretRedactionContext.cs`; `Apps/Terminal/Execution/ExportContextCommandHandler.cs` | `Application/Context/ProjectSelectionSpec.cs`; `Application/Secrets/SecretRedactionContext.cs`; `Apps/Mcp/McpProjectService.cs` | Selection carries the toggles, the redaction context resolves mandatory features, and a surface composes the prepared output. |
| Determine `changes` selection across nested Git repository boundaries. | `Application/Context/GitScopeSelection.cs`; `Infrastructure/FileSystem/GitScopePathProvider.cs`; `Apps/Terminal/Execution/TerminalProjectContextFactory.cs` | — | The token contract, safe Git query, and final intersection with effective selection are separate decisions. |
| Explain the lifetime of a stored MCP pack and how ranges are read. | `Apps/Mcp/DevProjexMcpTools.cs`; `Apps/Mcp/McpPackRegistry.cs` | — | The tool chooses inline versus stored output; the registry owns bounded persistence and range reads. |
| Trace an explicit remote source through clone identity and cache reuse. | `Apps/Mcp/McpProjectSourceResolver.cs`; `Infrastructure/Git/GitRepositoryService.cs`; `Infrastructure/Git/RepoCacheService.cs` | — | URL policy, network Git execution, and cache identity/lease handling must all be present. |

### Repomix tasks

| Task | Required files | Alternative sufficient set | Why these files are required |
|---|---|---|---|
| Establish Git-frequency ordering and the no-Git fallback. | `src/core/output/outputSort.ts`; `src/core/git/gitLogHandle.ts` | — | One file applies ordering and fallback, while the other obtains bounded history data. |
| Trace lexical and symlink confinement for MCP file access. | `src/mcp/pathScope.ts`; `src/mcp/tools/mcpToolRuntime.ts` | — | Path normalization/confinement and the tool runtime's enforcement point are both needed. |
| Explain how a remote configuration becomes trusted without silently executing an unreviewed symlink. | `src/cli/actions/remoteAction.ts`; `src/cli/prompts/remoteConfigTrustPrompt.ts`; `src/cli/prompts/remoteConfigTrustStore.ts` | — | The remote flow, review policy, and persisted identity form one decision chain. |
| Trace a local pack from collection through transformation to rendering. | `src/core/packager.ts`; `src/core/file/fileProcess.ts`; `src/core/output/outputGenerate.ts` | — | These files own orchestration, per-file processing, and final serialization. |
| Explain how the MCP `pack_codebase` call narrows its root and invokes packing. | `src/mcp/tools/packCodebaseTool.ts`; `src/mcp/tools/mcpToolRuntime.ts`; `src/mcp/pathScope.ts` | — | Schema/handler, common runtime, and root jail jointly define the call. |

### Flask tasks

| Task | Required files | Alternative sufficient set | Why these files are required |
|---|---|---|---|
| Trace blueprint error-handler registration and request-time selection. | `src/flask/sansio/blueprints.py`; `src/flask/sansio/app.py`; `src/flask/app.py` | — | Blueprint state is merged into the sans-I/O application and selected by the concrete request dispatcher. |
| Find where a session cookie is signed and which configuration keys affect saving it. | `src/flask/sessions.py`; `src/flask/app.py` | — | The session interface signs and writes the cookie; application defaults and response finalization select its inputs. |
| Explain CLI application discovery and application-factory invocation. | `src/flask/cli.py` | — | Discovery, factory parsing, `ScriptInfo`, and context setup are intentionally co-located. |
| Trace request/application context creation, session opening, and teardown. | `src/flask/ctx.py`; `src/flask/app.py` | — | Context state and application dispatch cooperate across these two files. |
| Explain how a custom JSON provider is selected and used. | `src/flask/sansio/app.py`; `src/flask/json/provider.py` | `src/flask/sansio/app.py`; `src/flask/json/__init__.py` | Application construction chooses the provider; the provider or facade defines serialization behavior. |

## Protocol

For every repository, the effective manifest, source bytes, `full` detail, per-file estimated cost, and existing greedy admission pass are held constant. The compared orders are current path order, Git activity only, unique resolved graph degree only, PageRank over the same deduplicated graph, and the combined candidate. Budgets are 4,000, 8,000, and 16,000 estimated tokens.

This recorded run is the protocol's primary `full`-detail series. Compact and signatures output were not used to select `importance-v1`: changing detail can change whether a required body is present and would therefore test a different gold condition.

For each task and budget the protocol records `Recall@B`, `AllRequired@B`, irrelevant-token share, and the oracle-feasible ceiling. `Recall@B` is the fraction of a task's smallest sufficient set admitted. `AllRequired@B` is one only when at least one complete sufficient set is admitted. A required file larger than the entire budget lowers the oracle ceiling rather than being counted as a ranking failure.

Weights are chosen on two repositories and evaluated on the held-out third, rotating the held-out repository. The fifteen task-budget rows are not treated as independent observations when one global order produces them. Gold paths never enter a scoring function. Historical fix commits are not used as labels, compressed output is not credited when the required body is absent, and Flask is not presented as a mixed-monorepo sample.

## Product algorithm

The frozen run was performed on 2026-09-07. The full-detail effective selection was built with the ordinary standard profile. For every repository, all five orders reused that one file manifest and exact `FileContentAnalyzer` character counts; the cost was the existing per-file `(characters + 3) / 4` estimate. No gold path was supplied to the ranker. Git used the last 200 commits by position. The graph included only unique, resolved, non-self edges.

The graph variant comparison tied at zero complete sufficient sets. PageRank won the next metric, aggregate mean `Recall@B`: `0.0519` against degree's `0.0444`. Degree had a nearly identical mean irrelevant-token share (`0.9789` against `0.9792`). Under the predeclared priority of `AllRequired@B`, then `Recall@B`, PageRank therefore became the `importance-v1` graph signal. Unsupported and extraction-failed files receive no PageRank signal.

The weight grid kept graph strictly larger than Git and limited role to `0.05`, `0.10`, or `0.15`. Selection on two repositories and evaluation on the third produced:

| Held-out repository | Training choice graph / Git / role | Held-out AllRequired / oracle-feasible | Mean Recall | Mean irrelevant tokens |
|---|---:|---:|---:|---:|
| DevProjex | `0.55 / 0.40 / 0.05` | `0 / 5` | `0.0000` | `1.0000` |
| Repomix | `0.85 / 0.10 / 0.05` | `0 / 9` | `0.0000` | `1.0000` |
| Flask | `0.85 / 0.10 / 0.05` | `0 / 2` | `0.0667` | `0.9412` |

After the leave-one-repository-out check, fitting the same constrained grid to all three registries selected graph `0.85`, Git `0.10`, and role `0.05`. These are the frozen `importance-v1` constants. The weak held-out results are recorded rather than described as a win: generic project-wide ranking did not admit a complete task set in this deliberately small full-detail budget range. Only 16 of 45 task-budget rows were oracle-feasible, chiefly because several required files individually cost more than 16,000 estimated tokens.

The product always uses rank normalization within the effective selection, scales the graph contribution by `(supported - extraction-failed) / candidates`, redistributes unavailable signal weight, and uses canonical relative path as the final tie-break. Git activity combines commit count and most-recent position in the fixed window. Manifests and inferred entry points receive a small lift; test sources receive a visible penalty but are never excluded.

Facts and emitted content are guarded as one per-file source version. Length and
last-write metadata are captured around indexing and checked when the coherent
content snapshot is opened, copied, and disposed. A concurrent change fails the
pack instead of combining scores from one version with bytes from another; this
uses the same metadata-freshness tradeoff as the existing content pipeline.

## Full-detail results

Each cell is `mean Recall · AllRequired/oracle-feasible · mean irrelevant-token share`, averaged over the five frozen tasks for that repository and budget. `Combined` uses the final `0.85 / 0.10 / 0.05` weights. Oracle feasibility is reported independently of the order.

| Repository / budget | Current | Git only | Degree only | PageRank only | Combined |
|---|---:|---:|---:|---:|---:|
| DevProjex / 4,000 | `0.0000 · 0/1 · 1.0000` | `0.0000 · 0/1 · 1.0000` | `0.0000 · 0/1 · 1.0000` | `0.0000 · 0/1 · 1.0000` | `0.0000 · 0/1 · 1.0000` |
| DevProjex / 8,000 | `0.0000 · 0/2 · 1.0000` | `0.0000 · 0/2 · 1.0000` | `0.0667 · 0/2 · 0.9769` | `0.0000 · 0/2 · 1.0000` | `0.0000 · 0/2 · 1.0000` |
| DevProjex / 16,000 | `0.0000 · 0/2 · 1.0000` | `0.0000 · 0/2 · 1.0000` | `0.0000 · 0/2 · 1.0000` | `0.0000 · 0/2 · 1.0000` | `0.0667 · 0/2 · 0.8612` |
| Repomix / 4,000 | `0.0000 · 0/1 · 1.0000` | `0.0000 · 0/1 · 1.0000` | `0.0000 · 0/1 · 1.0000` | `0.0000 · 0/1 · 1.0000` | `0.0000 · 0/1 · 1.0000` |
| Repomix / 8,000 | `0.0000 · 0/3 · 1.0000` | `0.0667 · 0/3 · 0.9041` | `0.0000 · 0/3 · 1.0000` | `0.0000 · 0/3 · 1.0000` | `0.0000 · 0/3 · 1.0000` |
| Repomix / 16,000 | `0.0000 · 0/5 · 1.0000` | `0.0667 · 0/5 · 0.9520` | `0.2333 · 0/5 · 0.8694` | `0.1667 · 0/5 · 0.9147` | `0.0000 · 0/5 · 1.0000` |
| Flask / 4,000 | `0.0000 · 0/0 · 1.0000` | `0.0000 · 0/0 · 1.0000` | `0.0000 · 0/0 · 1.0000` | `0.0000 · 0/0 · 1.0000` | `0.0000 · 0/0 · 1.0000` |
| Flask / 8,000 | `0.0000 · 0/0 · 1.0000` | `0.1000 · 0/0 · 0.8825` | `0.1000 · 0/0 · 0.9640` | `0.1000 · 0/0 · 0.9640` | `0.1000 · 0/0 · 0.8825` |
| Flask / 16,000 | `0.0000 · 0/2 · 1.0000` | `0.1667 · 0/2 · 0.7468` | `0.0000 · 0/2 · 1.0000` | `0.2000 · 0/2 · 0.9340` | `0.1000 · 0/2 · 0.9412` |

Coverage was `1,471 / 2,299` supported candidates for DevProjex (`63.98%`), `389 / 951` for Repomix (`40.90%`), and `80 / 212` for Flask (`37.74%`); all three extractions had zero failures and a complete 200-commit Git window. The low coverage is why graph weight is reduced at run time and the report names the measured share.

On the pinned DevProjex input the resulting report starts:

```text
[Ranking] importance-v1 · graph pagerank 64% of 2,299 sources · git window 200 commits · tests deprioritized
[Ranking top] Infrastructure/Git/GitRepositoryService.cs — dependents 21 · dependencies 6 · commits 7/200
[Ranking top] Apps/Terminal/Execution/TerminalServiceFactory.cs — dependents 40 · dependencies 60 · commits 3/200
```

These results are experimental. They show that importance order can recover individual required files that path order misses, but they do not establish better task completion. Callers with a seed should continue to use `search_project`, `get_file`, and `related_files`, then pass explicit `paths`; importance ranking is intended for seedless overview packs.
