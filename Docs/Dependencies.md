# Dependency Facts

DevProjex builds a read-only, static dependency index for the `related` CLI command and the
MCP `related_files` tool. The index is evidence-first: a missing declaration is not proof that a
reference is external, and an ambiguous name is never guessed into one target. It reads source and
configuration files only; it does not run MSBuild, TypeScript, Python, package scripts, source
generators, or project code.

## Evidence model

The current engine records two evidence layers:

- **ExplicitImport** covers TypeScript/JavaScript `import`, `export ... from`, and supported
  CommonJS `require` forms, plus Python `import` and `from ... import`. C# `using`, `global using`,
  and aliases supply lookup context but never create file edges by themselves.
- **TypeReference** covers C# type positions: field, property, parameter, return and base types;
  generic arguments and constraints; attributes; object creation; `typeof`, `sizeof`, `default`,
  casts, `as`, `is`, and declaration-pattern types. Bare identifiers, method names, variable names,
  string matches, and dependency-injection registrations are not edges.

Layer C semantic and runtime evidence is deliberately absent. Reflection, DI registrations,
templates, generated code, route discovery, Python import hooks, and other dynamic relationships are
therefore not inferred.

Each declaration is identified by scope, language, symbol kind, qualified name, generic arity, and
an optional file scope. Partial C# declarations share one identity with multiple source sites;
file-local types remain distinct even when their names match. Each reference retains its source line
and a compact source excerpt. Results use four statuses:

- **Resolved** — exactly one declaration or module in the allowed manifest is supported by the
  resolver evidence;
- **Ambiguous** — more than one manifest candidate remains, and every candidate is returned;
- **External** — a versioned platform catalog or declared external package proves that the target is
  outside the manifest;
- **Unresolved** — no supported rule proves a target, configuration is missing or legacy, a dynamic
  mechanism is involved, or a safety limit was reached.

## Selection and scope

There are three separate scopes. The **allowed manifest** is the effective DevProjex selection from
`ProjectContextPlan.IncludedFiles` and its `SourceRoot`. **Seed files** are the paths named by a
`related` or `related_files` request. **Candidate files** are related files projected from the index.
Seeds do not reduce the manifest that must be indexed. No declaration, candidate, path, or cached
reason can cross the current manifest gate, and self-file relationships are suppressed.

C# compilation scopes come from `.csproj` ownership and `ProjectReference` entries read as XML.
Global usings and aliases are shared within the owning scope; type parameters shadow global symbols;
nested generic names preserve the arity of every containing type. `InternalsVisibleTo` does not create
an edge, target-typed `new()` stays unresolved, and source-generator output is unavailable. Without
an owning `.csproj` inside the effective manifest, cross-file C# type references stay unresolved.

TypeScript and JavaScript use the nearest `tsconfig.json` or `jsconfig.json`. The resolver distinguishes
relative, bare, package-self, and `#imports` specifiers; performs `.js` to `.ts`, `.tsx`, and `.d.ts`
substitution; applies exact `paths` entries before wildcard entries; and respects `package.json`
`exports`, conditions, and explicit `null` blocking. Directory-index fallback is mode-dependent.
Literal `require(...)` calls are import evidence only in a supported CommonJS context; ESM contexts
remain unresolved. `node10` (including its `node` alias) and `baseUrl` are marked legacy under the
TypeScript 7 contract. DevProjex never guesses a `dist` to `src` mapping without configuration, and module references without an owning
`tsconfig.json` or `jsconfig.json` stay unresolved.

Python relative imports start at the source package. Regular and namespace-package portions are
combined, `.py` is preferred to `.pyi`, bounded static re-exports through `__init__` are followed, and
`__all__` affects wildcard imports only. Dynamic `__all__`, `setup.py`, and import hooks are not
executed and remain unresolved. Separate complete `sys.stdlib_module_names` snapshots cover Python
3.12 and 3.13. A decisive `requires-python`/`python_requires` constraint selects its snapshot;
otherwise only names found in both snapshots are classified as external.

External classification uses shipped, versioned evidence data: .NET `net10.0` reference-assembly
types, Python 3.12/3.13 standard-library module names, and Node 24 built-in modules. Declared Python
and Node package dependencies are additional external evidence. Merely failing to find a name in the manifest never
produces `External`.

## Extraction, limits, and diagnostics

C#, TypeScript/TSX/JavaScript, and Python adapters use shipped Tree-sitter grammars and embedded
`declarations.scm` and `references.scm` query data. Each supported source file is parsed once per
content fingerprint; its syntax tree is disposed immediately and only compact facts remain. Files
without an adapter are counted as unsupported instead of disappearing. Read, grammar, and query
failures are counted separately as extraction failures.

The default safety limits are 2 Mi characters per source file, 50,000 facts per file, 20,000 edges
per file, and 5,000,000 units of resolver work per index pass. A limit produces an explicit
`Unresolved` fact or extraction status with a reason; it is never reported as an empty successful
analysis.

The pinned C# grammar can report `ERROR` nodes for syntax it only partially recognizes. The engine
counts affected files and the named child-node kinds found below each `ERROR`. An error node is not
itself an extraction failure: facts outside the unsupported construct remain usable. In particular,
modern or incomplete constructs inside an error region may be missing or unresolved; DevProjex does
not repair the grammar or guess the relationship.

## Caches and determinism

File facts are cached by canonical physical path, content fingerprint, language, grammar version,
and query hash. Resolved indexes are cached by manifest generation, declaration-index revision, and
a resolver-configuration fingerprint covering `.csproj`/project references/global usings,
TypeScript configuration and package maps, Python configuration, and the TypeScript dialect.
Concurrent requests share one lazy computation. The default caches are bounded by both entry count
and estimated retained size: 64 MiB for compact file facts and 128 MiB for resolved edges. Eviction
changes latency, not results.

Changing one source reparses that source. Changing resolver configuration invalidates resolution but
reuses file facts, so no source parse is required. Before every result is exposed, it is gated against
the current manifest. Files, declarations, edges, candidates, reasons, and output groups are ordered
with ordinal portable paths, so input order, parallel scheduling, cache state, and host OS do not
change serialized results.

## User-facing results

`devprojex related PATH` and MCP `related_files` show dependencies, dependents, or both. Every row
contains a portable relative path, aggregated evidence reasons, resolution status, estimated tokens,
and a cross-scope marker when applicable. Ambiguous references remain one group with their candidate
list. Coverage reports manifest files, supported and unsupported languages, and extraction failures.
An unsupported seed is a successful empty result with an explicit diagnostic; a supported seed with
no edges reports that no related files exist in the effective selection.
