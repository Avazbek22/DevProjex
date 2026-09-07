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

The review run was frozen before execution and performed on 2026-09-07. Every order reused one standard-profile manifest, the same source bytes, `full` detail, the existing `(characters + 3) / 4` per-file estimate, and the unchanged greedy admission pass. Budgets were 4,000, 8,000, and 16,000 estimated tokens. The compared global orders were tree order, Git activity, unique resolved graph degree, PageRank, and the combined product order.

For each task and budget the protocol records `Recall@B`, `AllRequired@B`, irrelevant-token share, and the oracle-feasible ceiling. `Recall@B` is the best fraction admitted from a sufficient set. `AllRequired@B` is one only when one complete sufficient set is admitted. The oracle is feasible when the total cost of at least one sufficient set fits the budget; an individually oversized required file is therefore not charged to an ordering strategy.

The localization series also records a directed baseline. Its search vocabulary was fixed before the run from the task wording, never from ranked output: `LargestSkippedFiles`/`remaining budget`, `mandatory`/`SecretRedactionFeatures`, `GitScopeSelection`/`NestedRepository`, `pack_id`/`read_pack`, `repositorySourceUrl`/`RepoCache`; `gitSort`/`git log`, `symlink`/`path scope`, `remote config`/`trust`, `processFiles`/`generate output`, `pack_codebase`/`mcpToolRuntime`; `error_handler_spec`/`register_error_handler`, `save_session`/`SESSION_COOKIE`, `find_best_app`/`ScriptInfo`, `open_session`/`do_teardown`, and `json_provider_class`/`JSONProvider`. The scripted workflow performs search, takes its first five deterministic hits, follows resolved dependencies and dependents, then packs only those paths. It uses the same search and dependency services as the MCP tools, but the evaluation harness calls the service contracts directly rather than measuring JSON-RPC transport.

Gold paths never enter a score or search query. Historical fixes are not labels, one global order is not treated as 15 independent samples, compressed output is not credited when a required body is absent, and Flask is not described as a mixed monorepo. The three repository registries are used as groups; the original leave-one-repository-out weight selection remains the guard against answer leakage and pseudoreplication.

## Product algorithm after review

The `importance-v1` constants remain graph `0.85`, Git `0.10`, and role `0.05`; v5.2 is not released, so the corrected implementation retains the algorithm id. The graph is built once as canonical numeric node ids and sorted adjacency arrays. Only unique resolved non-self file pairs participate. The same graph supplies PageRank, dependents, dependencies, and coverage counts. PageRank uses 30 deterministic sequential iterations on two `double[]` buffers. Before dense-rank normalization, values are quantized to an absolute `1e-12` grid with midpoint-to-even rounding. This is a transitive equivalence relation; pairwise epsilon comparison is deliberately not used. At the graph sizes in this protocol, `1e-12` is well below a meaningful rank separation while absorbing observed symmetric-node noise near `1e-17`.

Extracted-facts coverage is `Supported / candidates`. `Supported`, `Unsupported`, and `ExtractionFailed` are mutually exclusive, so failures are not subtracted from `Supported`. Reports separately state facts coverage, unique resolved internal references over all non-external reference records, and the number of files touching a resolved edge. A high facts percentage can no longer be presented as high link coverage when the graph is sparse.

Git activity is commit count plus recency by position in a safe 200-commit LocalRead window. History is cached only after success, by repository identity, pinned HEAD, window, shallow state, and completeness. An incomplete shallow window has confidence `commits read / 200`; it is reported as, for example, `read 1/200 commits; shallow history`, and is not treated as evidence of low activity. Candidate repository boundaries are indexed once per operation. History paths are parsed as NUL-delimited fields; one Git-generated framing LF is removed from the first path field, while newline and `0x1e` bytes belonging to a filename are preserved.

Manifests and explicit `Main` or executable-module evidence are entry points. A source with no dependents and at least three dependencies is only a `coordinator`, not an inferred entry point. Tests are deprioritized but never excluded. The final tie-break is the canonical relative path.

Facts, preparation, cost, and emitted bytes are bound to one source identity. SHA-256 content plus length and last-write metadata are captured around fact indexing and checked while the coherent output snapshot is opened, copied, and disposed. A mismatch fails closed with guidance to repeat the export. Ranking still never widens the effective selection. Without `rank`, it performs no fact indexing, Git work, or content hashing and preserves the existing bytes.

### Missing-signal ablation

The fixed `0.85 / 0.10 / 0.05` weights were rerun under all three policies. Each row aggregates the nine repository/budget groups; `AllRequired` and oracle counts cover the underlying 45 task-budget cases.

| Policy | AllRequired / oracle-feasible | Mean Recall | Mean irrelevant tokens |
|---|---:|---:|---:|
| Renormalize available weights | `0 / 16` | `0.0333` | `0.9784` |
| Fill unavailable signals with neutral `0.5` | `0 / 16` | `0.0111` | `0.9935` |
| Limit score by available confidence | `0 / 16` | `0.0111` | `0.9935` |

Renormalization had better recall in this small sample, but it also reproduced the correctness defect: a manifest with only its role signal becomes `1.0`, above a strongly linked file whose graph confidence is 90%. Neutral fill avoids that exact maximum but still manufactures evidence. `importance-v1` therefore uses confidence limitation: missing weight contributes zero and the score cannot exceed available signal weight. The report names the policy and marks affected top entries `confidence limited`. This is a semantic choice favoring honest uncertainty over the `0.0222` aggregate recall advantage of renormalization; it is not presented as an empirical quality win.

## Architecture overview series

Before the run, a human central-file checklist was recorded for each repository: eight orchestration and boundary files in DevProjex, seven packing/config/MCP files in Repomix, and eight application/context/session/CLI files in Flask. The table counts checklist members in the first 15 non-test files; it is a manual sanity check, not task-completion evidence.

| Repository | Tree | Git only | Degree only | PageRank only | Combined |
|---|---:|---:|---:|---:|---:|
| DevProjex | `1/15` | `1/15` | `1/15` | `0/15` | `2/15` |
| Repomix | `0/15` | `0/15` | `3/15` | `1/15` | `1/15` |
| Flask | `0/15` | `6/15` | `7/15` | `6/15` | `7/15` |

The manual review calls the Flask combined list credible: it contains `app.py`, `sansio/app.py`, `ctx.py`, `sessions.py`, `cli.py`, helpers, wrappers, and JSON/application boundaries. DevProjex improves only modestly, surfacing `TerminalServiceFactory.cs` and `GitRepositoryService.cs` but missing several central files. Repomix is the counterexample: degree alone finds three checklist files, while PageRank and combined find only `mcpToolRuntime.ts`. Thus the combined order is not uniformly better even for seedless architecture review.

Measured coverage was:

| Repository | Facts-supported candidates | Resolved internal references | Files with resolved edges |
|---|---:|---:|---:|
| DevProjex | `1,471 / 2,299 (63.98%)` | `5,138 / 11,567 (44.42%)` | `1,221` |
| Repomix | `389 / 951 (40.90%)` | `1,025 / 2,612 (39.24%)` | `359` |
| Flask | `80 / 212 (37.74%)` | `175 / 512 (34.18%)` | `75` |

## Localization-task budget series

Each cell is `mean Recall · AllRequired/oracle-feasible · mean irrelevant-token share`, averaged over the five frozen tasks for that repository and budget. `Combined` is the confidence-limited product order. `Directed` varies by task because its explicit `paths` are the result of that task's fixed search and related-files workflow.

| Repository / budget | Tree | Git only | Degree only | PageRank only | Combined | Directed |
|---|---:|---:|---:|---:|---:|---:|
| DevProjex / 4,000 | `0.0000 · 0/1 · 1.0000` | `0.0000 · 0/1 · 1.0000` | `0.0000 · 0/1 · 1.0000` | `0.0000 · 0/1 · 1.0000` | `0.0000 · 0/1 · 1.0000` | `0.2000 · 0/1 · 0.9202` |
| DevProjex / 8,000 | `0.0000 · 0/2 · 1.0000` | `0.0000 · 0/2 · 1.0000` | `0.0000 · 0/2 · 1.0000` | `0.0000 · 0/2 · 1.0000` | `0.0000 · 0/2 · 1.0000` | `0.2000 · 0/2 · 0.9600` |
| DevProjex / 16,000 | `0.0000 · 0/2 · 1.0000` | `0.0000 · 0/2 · 1.0000` | `0.0000 · 0/2 · 1.0000` | `0.0000 · 0/2 · 1.0000` | `0.0000 · 0/2 · 1.0000` | `0.1333 · 0/2 · 0.8189` |
| Repomix / 4,000 | `0.0000 · 0/1 · 1.0000` | `0.0000 · 0/1 · 1.0000` | `0.0000 · 0/1 · 1.0000` | `0.0000 · 0/1 · 1.0000` | `0.0000 · 0/1 · 1.0000` | `0.0000 · 0/1 · 1.0000` |
| Repomix / 8,000 | `0.0000 · 0/3 · 1.0000` | `0.0667 · 0/3 · 0.9041` | `0.0000 · 0/3 · 1.0000` | `0.0000 · 0/3 · 1.0000` | `0.0000 · 0/3 · 1.0000` | `0.0000 · 0/3 · 1.0000` |
| Repomix / 16,000 | `0.0000 · 0/5 · 1.0000` | `0.0667 · 0/5 · 0.9520` | `0.0667 · 0/5 · 0.9547` | `0.1667 · 0/5 · 0.9147` | `0.0000 · 0/5 · 1.0000` | `0.1667 · 0/5 · 0.9662` |
| Flask / 4,000 | `0.0000 · 0/0 · 1.0000` | `0.0000 · 0/0 · 1.0000` | `0.0000 · 0/0 · 1.0000` | `0.0000 · 0/0 · 1.0000` | `0.0000 · 0/0 · 1.0000` | `0.2000 · 0/0 · 0.7001` |
| Flask / 8,000 | `0.0000 · 0/0 · 1.0000` | `0.1000 · 0/0 · 0.8825` | `0.0000 · 0/0 · 1.0000` | `0.1000 · 0/0 · 0.9640` | `0.0000 · 0/0 · 1.0000` | `0.2000 · 0/0 · 0.8520` |
| Flask / 16,000 | `0.0000 · 0/2 · 1.0000` | `0.1667 · 0/2 · 0.7468` | `0.1667 · 0/2 · 0.7468` | `0.2000 · 0/2 · 0.9340` | `0.1000 · 0/2 · 0.9412` | `0.6000 · 2/2 · 0.6201` |

Across the nine groups, combined mean recall is `0.0111`, below Git-only `0.0444`, PageRank-only `0.0519`, and directed `0.1889`. Combined admits no complete sufficient set; directed admits both oracle-feasible Flask sets at 16,000. This is a clear loss for generic ranking on localized tasks. The cause is not hidden: global importance rewards broadly connected and recently active infrastructure, while a task asks for a narrow semantic slice. Use `search_project` → `related_files` → `pack_context(paths: ...)` when a seed is known; importance ranking is an experimental seedless overview order.

On the pinned DevProjex corpus, the beginning of the reviewed report is representative:

```text
[Ranking] importance-v1 · graph pagerank · facts 64% of 2,299 sources · git window 200 commits · tests deprioritized · missing signals: confidence limited
[Ranking coverage] facts 64% · resolved internal references 5,138/11,567 (44%) · files with resolved edges 1,221
[Ranking top] Kernel/Models/IgnoreRules.cs — dependents 138 · dependencies 5 · commits 2/200 · priority 1; graph available; git available; main contribution: graph; confidence limited
```
