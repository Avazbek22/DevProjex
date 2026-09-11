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
- **TypeReference** covers C# type positions: field, local, `foreach`, property, event, indexer,
  parameter, method, local-function and delegate return, base and `catch` types; generic arguments
  and constraints; attributes; object creation; `typeof`, `sizeof`, `default`, casts, `as`, `is`,
  declaration-pattern types, and type arguments written on a generic method call. A call such as
  `AddScoped<IService, Service>()` therefore supplies two syntactic type references, but the method
  name is not a reference and the edge does not claim that a runtime container will bind those types.
  Tuple element names, bare identifiers, method names, variable names, and string matches are not edges.

Layer C semantic and runtime evidence is deliberately absent. Runtime DI behavior,
templates, generated code, route discovery, Python import hooks, and other dynamic relationships are
therefore not inferred.

Each declaration is identified by scope, language, symbol kind, qualified name, generic arity, and
an optional file scope. Partial C# declarations share one identity with multiple source sites;
file-local types remain distinct even when their names match. A resolved edge to such a partial
identity keeps one canonical target and the complete declaration-file list. Related-file projections
show every declaration file as a resolved part of the same symbol, in both directions; ambiguous
candidates remain a separate concept. Each reference retains its exact source occurrence, lexical
owner, source line, and a compact source excerpt; two same-name references on one line are not
deduplicated before resolution. Results use four statuses:

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
Both `/` and `\` in an MSBuild `Include` are normalized as project-reference separators on every OS;
this normalization never applies to ordinary Unix filenames. Project-reference visibility is
transitive, matching the SDK default: if A references B and B references C, source in A can resolve
declarations from C. A literal `DisableTransitiveProjectReferences=true` narrows the owning project's
visibility to itself and its direct project references; literal `false` and the SDK default remain
transitive. A non-literal value is diagnosed and treated as absent because DevProjex does not evaluate
MSBuild properties. Static `Compile Include`, `Compile Remove`, and linked-file membership likewise
remain unsupported: the affected compilation scope stays unresolved instead of guessing membership.
Global usings and aliases are shared
within the owning scope. Non-global usings and aliases apply only inside their compilation-unit or
namespace-block lexical scope; repeated blocks for the same namespace do not leak aliases into one
another. An alias is expanded only when it is the first component of the reference, and `global::`
bypasses alias expansion. Type parameters shadow global symbols only inside the lexical span of their
declaring type or method, while a qualified name is never suppressed by its final component.
The `global::` qualifier is retained as absolute-lookup evidence: it bypasses type-parameter
shadowing and never falls back through the source namespace or imported namespaces;
nested generic declarations preserve the arity of every containing type. A reference to a nested
segment after a generic container stays `Unresolved` until segment-aware binding is available.
Generic aliases retain both their container and argument references, relative alias targets can fall
back through the lexical namespace, and `using static` exposes nested types. `InternalsVisibleTo`
does not create an edge, target-typed `new()` stays unresolved, and source-generator output is unavailable. A simple
type name can resolve only to a declaration in the current or an enclosing namespace, an exactly
imported namespace, the current type's nesting chain, or the global namespace. Importing `Company`
does not expose `Company.Internal`, and a sole same-named declaration elsewhere in the project is not
guessed as the target. Current and enclosing namespaces take precedence over imported namespaces;
multiple visible imported declarations remain ambiguous. Namespace lookup considers only immediate
members of that namespace: a nested type is visible through an explicit qualification such as
`Holder.Task` or through the source type's nearest-to-farthest containing-type chain. Without an
owning `.csproj` inside the effective manifest, cross-file C# type references stay unresolved.
Declaration accessibility is not modeled across project boundaries; candidates that differ only by
that unavailable evidence remain `Ambiguous`. Files containing conditional-compilation directives
do not claim configuration-specific type edges: their type references stay `Unresolved` because no
`DefineConstants` set is available.

TypeScript and JavaScript use the nearest `tsconfig.json` or `jsconfig.json`. The resolver distinguishes
relative, bare, package-self, and `#imports` specifiers and follows ordered substitution: the first
existing probe wins, so multiple files found later in the same probe sequence are not ambiguity.
Module specifiers are read from parsed `import`, `export`, dynamic `import(...)`, and supported
literal `require(...)` syntax, including side-effect imports. A variable or template expression in
place of a string literal remains `Unresolved`; it is never treated as a guessed path.
`.js`, `.jsx`, `.mjs`, and `.cjs` specifiers probe their TypeScript and declaration counterparts before the
literal JavaScript file; `.jsx` probes `.tsx` first. Query and fragment suffixes on a relative module
URL remain part of the evidence while its physical path is probed without the suffix. Extensionless imports and directory indexes always probe `.js` and `.jsx`
after `.ts`, `.tsx`, and `.d.ts`; `allowJs` controls compilation membership, not resolution of files
already present in the manifest. Exact `paths` entries precede wildcard entries; among matching
wildcards, the longest prefix before `*` wins. Only that pattern's targets are tried, in declaration
order. A wildcard whose prefix and suffix overlap in the specifier is not a match; the same guard
applies to package maps. `package.json` `exports`, conditions, and explicit `null` blocking remain authoritative.
An existing priority `paths` target outside the allowed manifest stops fallback without exposing that
path. Invalid `exports` targets cannot leave their package, and exact exports never gain a directory-index
fallback. A bare `#imports` target is classified from declared external-package evidence rather than
being probed as a local filename. Ordered exports arrays remain unsupported and therefore unresolved.
When `compilerOptions.moduleSuffixes` is present, every path probe applies its suffixes in declared
order; an empty suffix is the explicit unsuffixed fallback. Thus `[".ios", ""]` selects `v.ios.ts`
before `v.ts`. A non-string entry makes the configuration unsupported instead of silently reverting
to unsuffixed resolution.
When `compilerOptions.rootDirs` is present, relative imports use the configured directories as one
virtual tree. A path found under exactly one root resolves normally; paths present under multiple
roots remain `Ambiguous`. The usual extension, directory-index, and `moduleSuffixes` probe order is
preserved. Roots are relative to the configuration file that declared them, including through
`extends`; roots outside the project or escaping it through a symbolic link are ignored and reported
with a constant configuration diagnostic.
Conditional package targets distinguish syntax from the source module kind: runtime `import(...)`
selects the `import` condition even in a `.cts` or `.cjs` file, while literal `require(...)` selects
the `require` condition. The `node` condition is active only in Node resolution modes, not in bundler
mode; inactive unknown conditions do not block a later `default`. When `customConditions` is
configured, only imports that traverse conditional package `exports` or `imports` are unresolved;
relative imports and configured `paths` keep their normal resolution behavior.
Directory-index fallback is allowed by `node10` and `bundler`; under `node16`/`nodenext`, an ESM
relative import needs an explicit extension while a supported CommonJS context can use extensionless
and directory probes. A relative directory containing `package.json` stays unresolved because
`main`/`types` entry-point semantics are not emulated; its `index.*` file is not guessed instead.
`.mts`/`.mjs` are ESM, `.cts`/`.cjs` are CommonJS, and ordinary
`.ts`/`.tsx`/`.js`/`.jsx` files default to CommonJS unless the nearest `package.json` has
`"type": "module"`. Literal `require(...)` calls are import evidence only in such a CommonJS context.
`moduleResolution` accepts exactly `node10` (including its `node` alias), `classic`, `node16`,
`nodenext`, and `bundler`, case-insensitively; any other value is reported as unsupported semantics.
When `moduleResolution` is absent, `module: node16` or `module: nodenext` selects the matching
resolution mode; other module values keep the existing bundler default.
`node10`/`node` and `baseUrl` are marked legacy under the TypeScript 7 contract.
DevProjex never guesses a `dist` to `src` mapping without configuration.

Without an owning `tsconfig.json` or `jsconfig.json`, one narrow capability applies to module
specifiers. A literal relative specifier, one that starts with `./` or `../`, is resolved when it
names a file already in the allowed manifest with the exact extension it wrote, such as
`./util.mjs`, `../lib/x.js`, or `./x.ts`. A relative specifier that names a directory resolves to
that directory's index file when the manifest holds exactly one of `index.ts`, `index.tsx`,
`index.d.ts`, `index.js`, and `index.jsx` there. That is the same index list, in the same order,
that a configured project probes, so an unconfigured project never resolves a directory a
configured project would refuse.

The directory rule withdraws wherever the answer would depend on configuration: a directory that
owns a `package.json` stays unresolved because `main` and `types` are not emulated, a directory
whose stem also names a sibling module such as `util.js` beside `util/index.js` stays unresolved
because module resolution settings decide that order, and two index files in one directory stay
unresolved. A `require(...)` call keeps its CommonJS-context rule, and an invalid `package.json`
above the importing file suppresses resolution exactly as it does with configuration. Everything
else keeps the reason `no owning tsconfig.json or jsconfig.json in the manifest`: an extensionless
specifier that names no such directory, a `.js` specifier whose only counterpart is a `.ts` file,
a path that leaves the project root, a bare specifier including one that merely begins with a dot
such as `.config/app.js`, a `#imports` specifier, and every form of `paths`, `rootDirs`,
`moduleSuffixes`, or `package.json` `exports` resolution. This path reads the manifest and the
package-map boundary only; it substitutes no extension, so it cannot change what a configured
project resolves.

TypeScript configuration supports a bounded `extends` chain when every value is one explicit relative
path inside the project root. At most eight inheritance edges are followed; cycles, arrays, package or
bare specifiers, and paths outside the root are reported as unsupported semantics. Child
`compilerOptions` replace inherited values by key. Inherited `paths` and `baseUrl` remain relative to
the configuration file that declared them, matching TypeScript's configuration-origin semantics;
the existing legacy `baseUrl` resolution limitation described above still applies.
Every extended file is included in the configuration fingerprint. A missing base is also recorded as
an absent control file, so its later appearance invalidates a cached dependency snapshot. When an
existing base is outside the effective manifest, the manifest-snapshot shortcut is bypassed; this
keeps later edits observable without widening the selected dependency manifest.

Go has one narrow capability: a package is a directory, so a name declared at the top level of
one file is visible to its siblings without an import, and that is the relationship the adapter
makes resolvable. Top-level `func`, method and `type` declarations are importable names within
their directory, and a type reference resolves to the file in the same directory that declares
it. A name declared in two directories stays two declarations, so a reference never reaches
across packages; it resolves to the one in its own directory or to nothing.

Everything else in Go is outside this capability and produces no edge rather than a guessed one.
Import paths are not resolved: `go.mod` is not read, a module path is not mapped to a directory,
and vendor directories, build tags, import aliases, dot imports and `internal` visibility are not
interpreted. Package-level `const` and `var` declarations are not yet importable names, and a
named type is recorded as one declaration without distinguishing struct, interface and alias.
Go needs no configuration file, so a Go file has no owning-configuration failure mode. A
predeclared type such as `string` or `error` names no file and produces no reference, a
declaration's own name is not a reference to itself, and a qualified reference such as
`alpha.Shared` names another package and is dropped rather than matched against a same-named
type in the referring directory.

One consequence is worth stating because it is not visible in the edges. A Go file now counts as
`Supported`, and extracted-facts coverage is supported files over candidates, so a selection
containing Go reports higher coverage and gives the graph signal more weight in importance
ranking than it did while Go was unsupported. That is the intended effect of adding an adapter,
but Go's graph is package-local by construction, so its coverage is not comparable with the
cross-file graphs the other four languages build.

Configuration reads have four explicit outcomes: valid, missing, corrupt, and unsupported semantics.
A malformed JSON document, a `null` or non-object `compilerOptions`, or an unsupported value shape is
never replaced by an implicit default. References whose resolution depends on that control file remain
`Unresolved` with its diagnostic. CLI facts coverage projects each failure as a bounded, safe object with
`path`, `problem`, and `affectedScopes`; raw exception text and scope identifiers are not emitted. Text
output uses the same three values in a trusted `[Dependency configuration]` line. Every `.csproj`,
`tsconfig.json`, `jsconfig.json`, `package.json`,
`pyproject.toml`, and `setup.cfg` is limited to 4 MiB. One operation reads and verifies each control file
once, then derives all scope, package-name, package-map, and external-package projections from that same
snapshot, so a result cannot mix two versions of one configuration file. UTF-8 control files are
accepted with or without a BOM; malformed byte sequences remain corrupt.
`pyproject.toml` is parsed as TOML, including multiline dependency arrays, optional dependency tables,
Poetry dependency tables, extras, environment markers, and named direct-URL requirements. Malformed
TOML is corrupt rather than a partially accepted configuration. When multiple otherwise-valid owning
configuration files of one language have the same root, ownership is diagnosed as unsupported instead
of choosing one by filename order. A nested Python configuration owns only its subtree; files outside
that subtree retain the root fallback scope.

Python relative imports start at the source package. `from module import Name` first checks classes,
functions, and static import aliases provided by either an ordinary module or a package initializer.
Only module-level class and function declarations provide importable names; a method or nested class
cannot satisfy `from module import Name`. Imports inside a function or class still create a dependency
from their file to the imported module, but they do not become names exported by the containing
module. Import syntax is read from parsed nodes, so parenthesized
multiline lists, comments, aliases, relative forms, and wildcard imports have the same semantics as
their single-line forms.
Only a package may then fall back to a child module of that name. Regular and namespace-package portions are
combined as package entities rather than being represented by an arbitrary file under the namespace;
a requested child is resolved to that child. A package initializer takes precedence over a same-named module file. Within a package,
a statically provided or re-exported name is resolved before a same-named child module. `.py` is
preferred to `.pyi`, bounded static re-exports through `__init__` are followed, and the last repeated
unconditional binding wins. A conditional re-export remains unresolved because its active branch is
not evaluated. A direct dotted import binds its top-level name unless it has an alias.
Every dotted segment must remain within the regular or namespace package selected by its parent; an
ordinary module cannot acquire children from a same-named directory. `__all__` affects
wildcard imports only. A missing imported name remains `Unresolved` with a constant reason instead of
turning the existence of the module into evidence for that name. Relative imports that would escape the top-level package remain unresolved.
Only a module-level assignment can establish `__all__` policy; docstrings and assignments inside a
function or class do not. Module-level value assignments and wildcard re-export expansion are not
indexed, and report explicit unresolved limitations rather than guessed bindings. Dynamic `__all__`,
`setup.py`, and import hooks are not executed and remain unresolved. Separate
complete `sys.stdlib_module_names` snapshots cover Python
3.12 and 3.13. A decisive `requires-python`/`python_requires` constraint selects its snapshot;
otherwise only names found in both snapshots are classified as external.

External classification uses shipped, versioned evidence data: .NET `net10.0` reference-assembly
types, Python 3.12/3.13 standard-library module names, and Node 24 built-in modules. Declared Python
and Node package dependencies are additional external evidence. Merely failing to find a name in the manifest never
produces `External`.

## Extraction, limits, and diagnostics

C#, TypeScript/TSX/JavaScript, Python, and Go adapters use shipped Tree-sitter grammars and embedded
`declarations.scm` and `references.scm` query data. Each supported source file is parsed once per
content fingerprint; its syntax tree is disposed immediately and only compact facts remain. Files
without an adapter are counted as unsupported instead of disappearing. Read, grammar, and query
failures are counted separately as extraction failures.
The same per-file handling applies before language dispatch: if an unsupported file disappears or its
metadata cannot be read after selection, it is reported as a transient extraction failure and is not
retained in the facts cache; it does not abort the rest of the index.

The default safety limits are 2 Mi characters per source file, 50,000 useful facts per file,
1,000,000 raw query captures per file, 20,000 edges per file, and 5,000,000 units of resolver work
per index pass. Query captures that do not describe a supported declaration, import, or reference do
not consume the useful-fact budget. The separate raw-capture ceiling bounds traversal of noisy syntax;
reaching either fact ceiling produces `ExtractionFailed` with `fact limit exceeded`, never a supported
file with silently missing dependencies. Any limit produces an explicit
`Unresolved` fact or extraction status with a reason; it is never reported as an empty successful
analysis. Source decoding is bounded by decoded characters rather than bytes: UTF-8, UTF-16, and
UTF-32 BOMs are honored, incomplete sequences fail closed, and reading stops as soon as the engine
has proved that the character limit is exceeded instead of scanning the rest of the file. Decode
buffers start from the opened source size and pooled byte/character rentals are capped at 64 KiB;
larger decoded text grows outside the shared pool, so one large file cannot retain a multi-megabyte
pooled character array for later workers.

TreeSitter.DotNet 1.3.0 exposes neither a parser timeout nor a cancellation flag. Cancellation is
checked before native parsing and every 256 captures during traversal. Native work is bounded by the
existing 2 Mi-character per-file limit, but cancellation requested inside one native parse is observed
only after that parse returns; the engine does not claim immediate native cancellation.

The resolver work limit is an admission budget applied in canonical file order, not a latch that
stops all later files after one rejection. A file is admitted only when all of its known import,
reference, and declaration-candidate visits fit the remaining budget. Candidate fan-out is counted
before resolution, so one reference cannot evade the limit by scanning thousands of same-name
declarations. For example, with a limit of 10 and file costs 8, 4, and
1, the first file is resolved, the second is marked `Unresolved` with `index work limit exceeded`,
and the third is resolved from the two remaining units. Rejected work is never executed.

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
changes latency, not results. The resolved-index estimate includes every transitively retained file
fact, including declarations and all declaration sites, even when the graph has few or no edges.
Manifest-snapshot eviction entries are generation-bound and removed together with their live
snapshot, so repeated rebuilds of the same cache keys cannot grow bookkeeping outside the limit.

Access failures and other transient I/O failures in either source or control files are not retained in
the prepared-source, resolved-index, or manifest-snapshot caches. A later request retries extraction
and configuration reading even when file stamps are unchanged. An expected control file observed as
absent is cacheable: its canonical path is part of the snapshot, and the snapshot is rejected as soon
as that path appears. Stable outcomes such
as invalid configuration syntax, an unsupported language, a content parse failure, or a safety limit
remain cacheable.

The engine has two metadata shortcuts above those content-fingerprinted facts: the extractor retains
prepared decoded text, and the engine retains resolved snapshots for a manifest. Importance ranking
supplies the SHA-256 identity it already captured from each source to both shortcuts, so a cache hit
requires matching file metadata and the same opaque content identity. `related_files` and
`devprojex related` do not compute those hashes; their warm calls intentionally keep the metadata-only
length, modification-time, and creation-time compromise. A same-length replacement whose timestamps
are deliberately restored can therefore remain cached for those two related-file surfaces until the
entry is evicted or its metadata changes.

The content identity passed to the extractor is an observed cache label; the extractor does not hash
the bytes again while reading. An adversarial `hash A -> read B -> restore A -> hash A` sequence between
the two observations is therefore not detected. This is a known coherence limitation, not evidence
that the intermediate bytes belonged to identity A.

Changing one source reparses that source. Changing resolver configuration invalidates resolution but
reuses file facts, so no source parse is required. Before every result is exposed, it is gated against
the current manifest. Files, declarations, edges, candidates, reasons, and output groups are ordered
with ordinal portable paths, so input order, parallel scheduling, cache state, and host OS do not
change serialized results.

The index pass canonicalizes each manifest path once and keeps that order through extraction and
resolution. Resolver configuration prepares reusable scope ownership, C# namespace/alias/type-parameter,
TypeScript path-precedence, Python module, and namespace-package indexes instead of searching the same
collections for every fact. The manifest gate reuses unchanged `FileFacts` and the published path index;
ranking consumes that same index. These are implementation details only: cache hits and optimized paths
must produce the same statuses, targets, candidates, evidence, and ordering as an uncached pass.
Opt-in dependency diagnostics count canonical path normalization, manifest sorting, dictionary and graph
construction, gated fact clones, resolver candidate probes, and cache hits. No project text is recorded,
and when measurement is inactive hot resolver loops do not call the counter path.

## User-facing results

`devprojex related PATH` and MCP `related_files` show dependencies, dependents, or both. Every row
contains a portable relative path, aggregated evidence reasons, resolution status, estimated tokens,
and a cross-scope marker when applicable. Ambiguous references remain one group with their candidate
list. Coverage reports manifest files, supported and unsupported languages, and extraction failures.
File-status reasons and configuration diagnostics use a fixed vocabulary; project-controlled mapping
keys and paths remain in their dedicated structured fields rather than being interpolated into trusted
diagnostic text. Edge evidence is project data: it deliberately includes the referenced symbol or
module specifier together with its source line. MCP keeps that evidence inside the untrusted-data block,
and CLI JSON returns it as result data.
An unsupported seed is a successful empty result with an explicit diagnostic; a supported seed with
no edges reports that no related files exist in the effective selection. At most eight configuration
diagnostic lines are rendered in CLI text output; JSON retains the complete safe array.
