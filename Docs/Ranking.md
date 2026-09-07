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

For each task and budget the protocol records `Recall@B`, `AllRequired@B`, irrelevant-token share, and the oracle-feasible ceiling. `Recall@B` is the fraction of a task's smallest sufficient set admitted. `AllRequired@B` is one only when at least one complete sufficient set is admitted. A required file larger than the entire budget lowers the oracle ceiling rather than being counted as a ranking failure.

Weights are chosen on two repositories and evaluated on the held-out third, rotating the held-out repository. The fifteen task-budget rows are not treated as independent observations when one global order produces them. Gold paths never enter a scoring function. Historical fix commits are not used as labels, compressed output is not credited when the required body is absent, and Flask is not presented as a mixed-monorepo sample.

## Product algorithm

Results and the selected constants are recorded here after the frozen runs. The product always uses rank normalization within the effective selection, a graph contribution scaled by supported extraction coverage, redistribution when a signal is unavailable, and canonical relative path as the final tie-break. PageRank is retained as an evaluation comparator; it is not automatically a product signal.
