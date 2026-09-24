# Dependency facts spike: measurements for issue #193

## Executive result

This is a disposable measurement prototype, not a production dependency engine. It parses source once, keeps compact declaration/import/type-reference facts, resolves those facts separately, and never builds or executes a corpus repository.

The useful result is mixed:

- explicit module imports are high-signal, especially in TypeScript;
- type-position-only Layer B produced no false resolved C# file edge in the 18 manually checked resolved bindings, but incomplete platform-symbol knowledge caused two wrong `Unresolved` statuses;
- configuration ownership and an explicit `Ambiguous` state prevent several otherwise plausible false edges;
- occurrence-based incoming rankings are heavily biased toward test helpers and frequently referenced DTO/contract types;
- a contract for `related_files` must preserve evidence, resolution status, candidates, and reason. A bare list of paths would conceal the important failure modes measured here.

Layer C was not implemented.

## Method

The prototype is a .NET 10 console project outside the solution. It references `Infrastructure.csproj` and obtains the shipped Tree-sitter grammars through `EmbeddedGrammarLibraryLocator`; it neither copies grammars nor downloads language packages. Queries in `queries/<language>/` extract declarations and references for C#, TypeScript/TSX/JavaScript, and Python.

The index has two stages:

1. enumerate supported files in ordinal path order, hash each file, parse it once, extract compact facts, dispose the syntax tree immediately;
2. merge symbol declarations and resolve imports/type references using repository configuration read as data.

The persisted result is sorted before JSON serialization. Timings are wall-clock time for a fresh process and peak memory is `Process.PeakWorkingSet64`. A syntax-error file means Tree-sitter reported an error node; it is distinct from an extraction exception. Measurements ran on Windows 11 build 26200, .NET SDK 10.0.400, Intel Core i9-13900HX, 63.7 GiB RAM. No corpus was restored, built, or executed.

Implemented resolution boundaries:

- **C#:** `.csproj` ownership and `ProjectReference` visibility are read without MSBuild; using directives are lookup context, not edges; aliases, nested type names, generic arity, partial declaration merging, file-local scope, type-parameter shadowing, and target-typed `new()` as explicitly unresolved are represented. `InternalsVisibleTo` does not create an edge.
- **TypeScript/JavaScript:** nearest `tsconfig`, dialect, relative/bare/package-self/`#imports`, `.js` to `.ts`/`.tsx`/`.d.ts`, exact paths before wildcard paths, package maps including `null`, and mode-dependent index probing are represented. `node10` or `baseUrl` configuration is marked legacy. No `dist` to `src` guess is made.
- **Python:** relative imports, implementation-first `.py`/`.pyi`, namespace-package portions, bounded static re-exports, and wildcard-only `__all__` handling are represented. Setup scripts and import hooks are not executed. Standard-library and declared-package evidence is required for `External`; absence from the manifest alone yields `Unresolved`.

Known prototype approximations are recorded rather than hidden: C# global usings are only observed in their declaring file, the BCL inventory is deliberately small, TypeScript conditional exports use a fixed source-oriented condition order, Python dependency metadata is read conservatively, and source-generator output is unavailable. These are causes to model in a product contract, not reasons to guess.

## Corpus

| Repository | Language focus | Pinned commit |
|---|---|---|
| Avazbek22/DevProjex | C# | `9e8ea6bd44589d7270658854a4d025ad8a2375e8` |
| yamadashy/repomix | TypeScript/JavaScript | `85e3969b010c72b905203812d1a3f5beb84a2102` |
| pallets/flask | Python | `d318b683471101618febed18996405ad26462110` |

Each repository was cloned outside the DevProjex working tree and checked out detached at the pinned SHA. The temporary clones are intentionally absent after the measurement.

## Index measurements

### Repository totals

| Repository | Files | Extraction exceptions | Syntax-error files | Layer A | Layer B | Resolved | Ambiguous | External | Unresolved | Cold time | Peak memory |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| DevProjex | 1,391 | 0 | 388 | 10 | 82,640 | 26,351 (31.88%) | 1,296 (1.57%) | 40,506 (49.01%) | 14,497 (17.54%) | 52,254 ms | 126.0 MiB |
| Repomix | 410 | 0 | 3 | 2,776 | 3,592 | 2,927 (45.96%) | 15 (0.24%) | 1,311 (20.59%) | 2,115 (33.21%) | 2,373 ms | 65.4 MiB |
| Flask | 83 | 0 | 0 | 648 | 786 | 404 (28.17%) | 56 (3.91%) | 292 (20.36%) | 682 (47.56%) | 570 ms | 56.2 MiB |

The DevProjex time exposes a prototype scalability problem: simple-name resolution scans the declaration collection for every Layer B occurrence. The data model is viable, but production resolution needs keyed symbol indexes before any latency promise.

### Per language

| Repository / language | Files | Syntax-error files | Layer A | Layer B | Resolved | Ambiguous | External | Unresolved |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| DevProjex / C# | 1,388 | 388 | 0 | 82,630 | 26,351 | 1,296 | 40,499 | 14,484 |
| DevProjex / JavaScript | 2 | 0 | 5 | 6 | 0 | 0 | 4 | 7 |
| DevProjex / Python | 1 | 0 | 5 | 4 | 0 | 0 | 3 | 6 |
| Repomix / TypeScript | 397 | 3 | 2,741 | 3,591 | 2,926 | 15 | 1,283 | 2,108 |
| Repomix / JavaScript | 13 | 0 | 35 | 1 | 1 | 0 | 28 | 7 |
| Flask / Python | 83 | 0 | 648 | 786 | 404 | 56 | 292 | 682 |

C# `using` directives correctly contribute zero Layer A edges. The 388 C# syntax-error files did not cause extraction exceptions; this metric is too broad to use as a fail/accept gate, but it is useful diagnostic evidence for query coverage.

## Seed set

`seeds.json` was committed before rankings were run. It selects an entry point, central service, public contract, test, and routine DTO/utility for every repository.

| Repository | Seed | Outgoing facts: R/A/E/U | Unique resolved dependent files |
|---|---|---:|---:|
| DevProjex | `Apps/TerminalHost/Program.cs` | 3/0/2/4 | 0 |
| DevProjex | `Application/Services/ProjectContentMetricsCalculator.cs` | 12/0/14/5 | 0 |
| DevProjex | `Application/Compression/ICodeCompressor.cs` | 13/0/23/2 | 11 |
| DevProjex | `Tests/DevProjex.Tests.Unit/TreeSitterCodeCompressorTests.cs` | 37/0/63/72 | 1 |
| DevProjex | `Kernel/Contracts/TreeNodeDescriptor.cs` | 1/0/6/0 | 75 |
| Repomix | `src/index.ts` | 37/0/0/0 | 2 |
| Repomix | `src/core/packager.ts` | 41/0/1/9 | 19 |
| Repomix | `src/shared/types.ts` | 0/0/0/0 | 19 |
| Repomix | `tests/core/file/fileProcess.test.ts` | 29/0/3/2 | 0 |
| Repomix | `src/shared/patternUtils.ts` | 0/0/0/0 | 3 |
| Flask | `src/flask/__main__.py` | 1/0/0/0 | 0 |
| Flask | `src/flask/app.py` | 62/2/30/64 | 6 |
| Flask | `src/flask/typing.py` | 0/0/4/1 | 6 |
| Flask | `tests/test_basic.py` | 9/5/23/4 | 0 |
| Flask | `src/flask/helpers.py` | 10/0/10/45 | 10 |

`R/A/E/U` means `Resolved / Ambiguous / External / Unresolved`. Counts are fact occurrences; dependent counts are distinct source files.

## Manual review of 60 relationships

The following rows were checked against the pinned source. “Wrong” means the emitted status or target is wrong; it does not mean the source code is wrong.

### DevProjex (20)

| # | Direction and evidence | Result and reason | Verdict |
|---:|---|---|---|
| 1 | `Program.cs:15` `new TerminalApplication(...)` | Resolved to `TerminalApplication.cs`, one visible declaration | correct |
| 2 | `Program.cs:16` `new InvocationEnvironment(...)` | Resolved to `InvocationEnvironment.cs` | correct |
| 3 | `Program.cs:17` `new TerminalServiceFactory(...)` | Resolved to `TerminalServiceFactory.cs` | correct |
| 4 | `Program.cs:27` `new UTF8Encoding(...)` | Unresolved because the BCL catalog lacks this type | wrong; should be External |
| 5 | `ProjectContentMetricsCalculator.cs:9` return `ExportOutputMetrics` | Resolved to `ExportOutputMetricsCalculator.cs` | correct |
| 6 | `ProjectContentMetricsCalculator.cs:10` parameter `IFileContentAnalyzer` | Resolved to the Kernel contract | correct |
| 7 | `ProjectContentMetricsCalculator.cs:22` `ProjectCopyExportProgress` | Resolved to `ProjectCopyExportModels.cs` | correct |
| 8 | `ProjectContentMetricsCalculator.cs:34` `ContentFileMetrics` | Resolved to `ExportOutputMetricsCalculator.cs` | correct |
| 9 | `ICodeCompressor.cs:30` parameter `CodeTransformKinds` | Resolved to `CodeCompressionModels.cs` | correct |
| 10 | `ICodeCompressor.cs:45` return `ICodeCompressionScope` | Resolved to the same declaration file | correct binding; file self-edge should be suppressed by a consumer |
| 11 | `ICodeCompressor.cs:55` `new NotSupportedException(...)` | Unresolved because the BCL catalog lacks this type | wrong; should be External |
| 12 | `ICodeCompressor.cs:78` `CodeCompressionAvailabilitySnapshot` | Resolved to `CodeCompressionModels.cs` | correct |
| 13 | compressor test `:257` `new TemporaryDirectory()` | Resolved to the unit-test helper | correct |
| 14 | compressor test `:596` `new CodeCompressionSession(...)` | Resolved to `CodeCompressionSession.cs` | correct |
| 15 | compressor test `:611` `new CodeCompressionContext(...)` | Resolved to `CodeCompressionSession.cs` | correct |
| 16 | `TreeNodeDescriptor.cs:9` child type `TreeNodeDescriptor` | Resolved to the same declaration file | correct recursive type |
| 17 | `ProjectContextDocumentService.cs:1692` parameter `TreeNodeDescriptor` | Incoming edge to the Kernel DTO | correct |
| 18 | `ProjectContextPlanner.cs:591` parameter `TreeNodeDescriptor` | Incoming edge to the Kernel DTO | correct |
| 19 | `ProjectAnalysisService.cs:460` parameter `TreeNodeDescriptor` | Incoming edge to the Kernel DTO | correct |
| 20 | `TreeAndContentExportService.cs:17` parameter `TreeNodeDescriptor` | Incoming edge to the Kernel DTO | correct |

### Repomix (20)

| # | Direction and evidence | Result and reason | Verdict |
|---:|---|---|---|
| 1 | `index.ts:4` export from `./core/packager.js` | Resolved via `.js` to `.ts` substitution | correct |
| 2 | `index.ts:5` type export from the same specifier | Resolved to `packager.ts` | correct; separate imported binding |
| 3 | `index.ts:8` export from `fileCollect.js` | Resolved to `fileCollect.ts` | correct |
| 4 | `index.ts:13` export from `fileTreeGenerate.js` | Resolved to `fileTreeGenerate.ts` | correct |
| 5 | `cliReport.test.ts:5` import from `src/index.js` | Incoming edge to `index.ts` | correct |
| 6 | `packager.ts:1` import `node:path` | External bare runtime module | correct |
| 7 | `packager.ts:2` import `configSchema.js` | Resolved to `configSchema.ts` | correct |
| 8 | `packager.ts:3` import `logger.js` | Resolved to `logger.ts` | correct |
| 9 | `packager.ts:4` import `memoryUtils.js` | Resolved to `memoryUtils.ts` | correct |
| 10 | `defaultAction.ts:14` import `packager.js` | Incoming edge to `packager.ts` | correct |
| 11 | `defaultAction.ts:19` import `shared/types.js` | Incoming edge to `types.ts` | correct |
| 12 | `defaultAction.ts:83` type `RepomixProgressCallback` | Incoming Layer B edge to `types.ts` | correct |
| 13 | `fileCollect.ts:5` import `shared/types.js` | Incoming edge to `types.ts` | correct |
| 14 | `fileProcess.test.ts:1` import `vitest` | External bare package | correct |
| 15 | `fileProcess.test.ts:2` import `fileManipulate.js` | Resolved to `fileManipulate.ts` | correct |
| 16 | `fileProcess.test.ts:3` import `fileProcess.js` | Resolved to `fileProcess.ts` | correct |
| 17 | `fileProcess.test.ts:4` import `fileTypes.js` | Resolved to `fileTypes.ts` | correct |
| 18 | `defaultAction.ts:18` import `patternUtils.js` | Incoming edge to `patternUtils.ts` | correct |
| 19 | `packCodebaseTool.ts:11` import `patternUtils.js` | Incoming edge to `patternUtils.ts` | correct |
| 20 | `patternUtils.test.ts:2` import `patternUtils.js` | Incoming edge to `patternUtils.ts` | correct |

### Flask (20)

| # | Direction and evidence | Result and reason | Verdict |
|---:|---|---|---|
| 1 | `__main__.py:1` `from .cli import main` | Resolved to `cli.py` | correct |
| 2 | `app.py:3` `import collections.abc` | External, known standard library | correct |
| 3 | `app.py:13` `from types import TracebackType` | Unresolved because `types` is absent from the small stdlib set | wrong; should be External |
| 4 | `app.py:17` `import click` | External, declared package | correct |
| 5 | `app.py:18` import from `werkzeug.datastructures` | External, declared package | correct |
| 6 | `flask/__init__.py:2` `from .app import Flask` | Incoming edge to `app.py` | correct |
| 7 | `typing.py:3` `import collections.abc` | External standard library | correct |
| 8 | `typing.py:7` import `_typeshed.wsgi` | Unresolved: no manifest/config evidence | disputed; type-checker ambient module is externally supplied |
| 9 | `app.py:33` `from . import typing as ft` | Incoming edge to `typing.py` | correct |
| 10 | `views.py:5` `from . import typing as ft` | Incoming edge to `typing.py` | correct |
| 11 | `test_basic.py:1` `import gc` | Unresolved because `gc` is absent from the small stdlib set | wrong; should be External |
| 12 | `test_basic.py:13` `import pytest` | External, dependency metadata evidence | correct |
| 13 | `test_basic.py:23` `import flask` | Resolved to `flask/__init__.py` | correct |
| 14 | `test_basic.py:24` import `flask.globals` | Resolved to `globals.py` | correct |
| 15 | `test_basic.py:25` import `flask.testing` | Resolved to `testing.py` | correct |
| 16 | `helpers.py:3` `import importlib.util` | Unresolved because `importlib` is absent from the small stdlib set | wrong; should be External |
| 17 | `helpers.py:12` import `werkzeug.utils` | External, declared package | correct |
| 18 | `helpers.py:17` `from .globals import _cv_app` | Resolved to `globals.py` | correct |
| 19 | `flask/__init__.py:13` re-export from `.helpers` | Incoming edge to `helpers.py` | correct |
| 20 | `app.py:40` import from `.helpers` | Incoming edge to `helpers.py` | correct |

Total: **54 correct, 5 wrong, 1 disputed**. All five wrong rows come from incomplete platform-module catalogs (two C# types and three Python modules). None emitted a false file target.

### C# Layer B noise

Among the 18 manually reviewed C# `Resolved` bindings above, **0/18 (0%)** pointed to a false file. Two self-file bindings are semantically valid symbol resolutions but are useless as `related_files`; a file-level projection should remove them. Two additional C# facts were status errors (`UTF8Encoding`, `NotSupportedException`) caused by incomplete BCL evidence and remained targetless, so they are not false edges.

The larger risk visible in the full result is ambiguity and occurrence inflation, not random resolved targets: simple-name collisions account for 1,296 C# `Ambiguous` facts, and repeated type positions can create hundreds of incoming occurrences from one large test file.

## DevProjex incoming ranking

Counts exclude file self-edges and count resolved fact occurrences, not unique source files.

### With tests

| Rank | Incoming | File | Manual classification of why it is referenced |
|---:|---:|---|---|
| 1 | 1,048 | `Tests/DevProjex.Tests.Terminal/TestInfrastructure.cs` | test utility and shared harness types |
| 2 | 1,020 | `Tests/DevProjex.Tests.Unit/Helpers/TemporaryDirectory.cs` | test resource-lifetime utility |
| 3 | 895 | `Tests/DevProjex.Tests.Integration/Helpers/TemporaryDirectory.cs` | integration-test utility |
| 4 | 892 | `Kernel/Contracts/TreeNodeDescriptor.cs` | central tree DTO/contract |
| 5 | 693 | `Kernel/Models/IgnoreRules.cs` | filtering DTO/value types |
| 6 | 639 | `Kernel/Models/IgnoreOptionId.cs` | filtering identifier/value type |
| 7 | 494 | `Kernel/Abstractions/IFileContentAnalyzer.cs` | cross-layer contract |
| 8 | 328 | `Kernel/Models/ProjectSelectionProfile.cs` | selection profile DTO |
| 9 | 316 | `Application/Context/ProjectContextPlan.cs` | orchestration plan DTO |
| 10 | 305 | `Application/Secrets/ISecretDetector.cs` | cross-layer contract |
| 11 | 291 | `Apps/Avalonia/ViewModels/TreeNodeViewModel.cs` | UI tree adapter/view model |
| 12 | 275 | `Kernel/Models/ScanResult.cs` | scanner result DTO |
| 13 | 272 | `Application/Services/FileContentAnalyzer.cs` | central service/utility |
| 14 | 272 | `Kernel/Models/PersistentSecretMarks.cs` | security-state DTO |
| 15 | 255 | `Application/Selection/SelectionRefreshSnapshot.cs` | selection snapshot DTO |

### Without test sources

| Rank | Incoming | File | Manual classification of why it is referenced |
|---:|---:|---|---|
| 1 | 259 | `Application/Context/ProjectContextPlan.cs` | orchestration plan DTO |
| 2 | 235 | `Kernel/Models/IgnoreRules.cs` | filtering DTO/value types |
| 3 | 204 | `Kernel/Abstractions/IFileContentAnalyzer.cs` | cross-layer contract |
| 4 | 192 | `Kernel/Contracts/TreeNodeDescriptor.cs` | central tree DTO/contract |
| 5 | 179 | `Application/Secrets/ISecretDetector.cs` | cross-layer contract |
| 6 | 177 | `Application/Context/ProjectSelectionSpec.cs` | input specification DTO |
| 7 | 158 | `Kernel/Models/IgnoreOptionId.cs` | filtering identifier/value type |
| 8 | 151 | `Infrastructure/ThemePresets/ThemePresetModels.cs` | theme DTO family |
| 9 | 128 | `Apps/Avalonia/ViewModels/TreeNodeViewModel.cs` | UI tree adapter/view model |
| 10 | 122 | `Application/Services/ProjectCopyExportModels.cs` | export progress/result DTOs |
| 11 | 120 | `Application/Compression/CodeCompressionModels.cs` | compression contracts and snapshots |
| 12 | 119 | `Apps/Terminal/Tui/TerminalWorkspaceCommand.cs` | TUI command/controller model |
| 13 | 109 | `Kernel/Models/PersistentSecretMarks.cs` | security-state DTO |
| 14 | 101 | `Kernel/Models/ScanResult.cs` | scanner result DTO |
| 15 | 100 | `Infrastructure/FileSystem/FileSystemScanner.State.cs` | scanner state/utility types |

This is useful evidence for #262: raw incoming occurrence count mostly identifies shared DTOs, interfaces, and test infrastructure, not architectural “importance”. A production ranking should expose both unique dependent files and occurrences, allow tests to be separated, and label the declaration kind.

## Comparison with codebase-memory-mcp

Comparator version `v0.10.8` was installed into a temporary directory using its pinned `install.ps1 --skip-config --dir=<temp>`. `CBM_CACHE_DIR` pointed to a private temporary directory. Windows Defender did not block the binary, no daemon was started, and only one-shot `cli` commands were used.

After `index_repository` and `get_graph_schema`, the spike queried `IMPORTS` and `USAGE` relations and collapsed endpoints by `file_path`. CBM is a comparator, not ground truth.

| Repository | CBM graph nodes | CBM graph edges | Partial parses | Captured file edges (`IMPORTS` / `USAGE`) |
|---|---:|---:|---:|---:|
| DevProjex | 63,354 | 300,520 | 21 | 19,595 (1,215 / 18,380) |
| Repomix | 11,956 | 21,841 | 6 | 2,575 (1,061 / 1,514) |
| Flask | 2,043 | 8,487 | 3 | 380 (160 / 220) |

On pairs incident to the same preselected seed files:

| Repository | Prototype pairs | CBM pairs | Both | Prototype only | CBM only |
|---|---:|---:|---:|---:|---:|
| DevProjex | 96 | 112 | 59 | 37 | 53 |
| Repomix | 90 | 87 | 81 | 9 | 6 |
| Flask | 40 | 93 | 32 | 8 | 61 |

Representative discrepancies were reviewed manually:

| Cause class | Discrepancy | Manual verdict |
|---|---|---|
| wrong scope | CBM reports `TerminalHost/Program.cs -> AnalyzeCommandHandler.cs`, although the file contains no such source-level reference | prototype is right to omit it |
| wrong scope | CBM reports `packager.ts -> .pinact.yaml`, `repomix.config.json`, and a test file as `USAGE` | prototype is right; these are not source dependency edges |
| module normalization | Prototype reports `index.ts -> core/packager.ts`; CBM reports extensionless `index.ts -> core/packager` | prototype target is the actual manifest file |
| incomplete configuration | Prototype marks `UTF8Encoding`, `NotSupportedException`, `gc`, and `importlib` unresolved | CBM/platform knowledge is better here; these are external platform symbols |
| ambiguous binding | the C# fixture has `Alpha.User` and `Beta.User` under two usings | prototype's `Ambiguous` plus candidates is safer than selecting one |
| dynamic behavior | Python fixture assigns `__all__ = build_exports()` | prototype returns `Unresolved`; neither static graph can prove runtime exports |
| unsupported syntax | C# target-typed `new()` has no written type | prototype returns `Unresolved` instead of guessing from assignment context |
| package rule | ESM import `./dir` with only `dir/index.ts` | prototype correctly rejects index fallback in the fixture's mode |
| package rule | package export `fixture/blocked` maps to `null` | prototype correctly returns `Unresolved` with a blocked-export reason |
| coverage | CBM finds Flask example-to-framework usages outside the five seed sources | valid extra discovery, but many are non-import `USAGE`; consumers need edge-class filtering |

The largest comparison lesson is that “more edges” is not equivalent to better file relations. CBM's broad `USAGE` improves recall but introduces cross-format and lexical noise; the prototype's restricted layers improve explainability but lose platform symbols and unsupported language mechanisms.

## Determinism and invalidation

Three DevProjex runs produced the identical result hash:

```text
normal:  2a3b7b22fad4565c0e68701e92a20d506f55215595c68a60160a73f5b25f60d3
cached:  2a3b7b22fad4565c0e68701e92a20d506f55215595c68a60160a73f5b25f60d3
reverse: 2a3b7b22fad4565c0e68701e92a20d506f55215595c68a60160a73f5b25f60d3
```

Timing and memory counters are excluded from the hashed payload. The cached run parsed 0 files and reused 1,391; the reverse run changed enumeration order before parsing. Both still re-resolved 1,391 files.

The TypeScript invalidation fixture produced:

```text
source-change parsed=1 reused=4 reresolved=5
config-change parsed=0 reused=5 reresolved=5
```

This demonstrates the needed separation: a source edit invalidates one parse; a `tsconfig paths` edit invalidates resolution without reparsing source.

## Fixtures retained for future work

Every fixture directory contains an `expected.json` with statuses, targets, reasons, or explicitly absent facts.

- C#: DI `AddScoped<IRepo, Repo>()` does not become a type edge; ambiguous `User`; a three-file partial type; two file-local `Helper` types; type-parameter shadowing; interface without implementation; target-typed `new()`; IVT without `ProjectReference`.
- TypeScript: `./x.js` to `x.ts`; ESM directory import without index fallback; `exports: null`; exact path before wildcard; config-only paths invalidation; legacy `node10`/`baseUrl` classification.
- Python: relative sibling import; namespace package with two portions; bounded static re-export through `__init__.py`; dynamic `__all__` for wildcard import.

`verify-fixtures` passes with zero failures.

## Decisions now supported by data

1. Make `Resolved`, `Ambiguous`, `External`, and `Unresolved` first-class result states; never encode uncertainty as a nullable target alone.
2. Include evidence layer, source line, compact source evidence, reason, and candidate targets in the engine contract.
3. Use symbol identity `ScopeId + LanguageId + SymbolKind + QualifiedName + GenericArity`, with multiple declaration sites. The partial and file-local fixtures require both parts.
4. Keep extraction and resolution caches separate. Configuration changes must not trigger reparsing.
5. Build keyed indexes before product work: by scope/language/simple name, qualified name, module path, and reverse target. The prototype's linear C# lookup is not shippable.
6. Keep Layer A and Layer B separately queryable. Layer A has stronger semantics; Layer B adds useful C# coverage but needs collision and platform handling.
7. Do not expose occurrence count as a single “importance” score. Report unique source files, occurrences, declaration kind, and test/non-test views.
8. Suppress same-file relations at the file projection while preserving the underlying symbol fact.
9. Treat compiler/runtime/platform catalogs as versioned evidence providers. Missing from the source manifest is never sufficient evidence for `External`.
10. Make configuration ownership and dialect part of the resolution reason. A path without its owning project/package context is not a reproducible fact.

## What these data do not settle

- precision/recall over a statistically representative labeled corpus;
- complete C# compilation semantics: global usings across files, conditional compilation, multi-targeting, generated sources, extern aliases, and full metadata references;
- complete Node resolution across all TypeScript modes, condition sets, workspaces, and package-manager layouts;
- complete Python environment discovery, editable installs, `.pth`, import hooks, and typed-package precedence;
- the safe scope and cost of a future Layer C;
- incremental persistence format, concurrency model, cancellation behavior, and memory under repositories much larger than these three;
- the product ranking formula for #262;
- whether CBM `USAGE` can be filtered into a comparably precise edge class.

The measurements support designing the contracts and indexes. They do not support promising production `related_files` quality yet.
