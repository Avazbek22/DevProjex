# Dependency language validation

This sequential scanner records facts, navigation, resolution counters and source evidence at fixed
repository commits without running project code. Supply a root, language, output path and three
sample paths. Git symlink entries represented as regular files in a Windows checkout must be
excluded with `--exclude=relative/path`; filesystem reparse points are always excluded.

## Bash corpus

Only `.sh` and `.bash` files are counted. Extensionless scripts and `.bats` tests are outside these
extension bindings, so these numbers are not an estimate for all shell text in each repository.

| Repository | Commit | Files | With facts | Literal sources | Expanded sources |
| --- | --- | ---: | ---: | ---: | ---: |
| Bash-it/bash-it | 4725d29db8c0ac8c21df47664b28539f3b8fce94 | 343 | 232 | 7 | 89 |
| nvm-sh/nvm | a4f801ed72d71b945a5c0e1b28a40eab581e7e94 | 6 | 6 | 1 | 0 |
| bats-core/bats-core | 2341486183f0b00a6e420770e1f2148758d29377 | 52 | 39 | 0 | 7 |
| git/git | 339ab2a8f14c0c304ae2f28df1a859f3d2cf610c | 1305 | 1226 | 1147 | 302 |

Across these files, 1155/1553 sources are literal (74.37%), 393/1553 contain variable or command
expansion (25.31%), and 5/1553 use other computed input (0.32%). This total is dominated by Git's
test scripts. Without Git, 96/104 sources are computed (92.31%). A literal argument establishes
source evidence, not its runtime file target. Absolute paths, unknown working directories and
shell search settings remain unresolved, so the literal count does not imply a project dependency.

Three files per repository were inspected:

- Bash-it: `bash_it.sh`, `completion/available/system.completion.bash`,
  `plugins/available/rbenv.plugin.bash`: no resolved edges, 17 unresolved groups.
- nvm: `nvm.sh`, `install.sh`, `test/common.sh`: no resolved edges, 3 unresolved groups.
- bats-core: `contrib/release.sh`, `lib/bats-core/test_functions.bash`,
  `lib/bats-core/tracing.bash`: no resolved edges, 6 unresolved groups.
- Git: `ci/run-build-and-tests.sh`, `ci/test-documentation.sh`, `t/t0000-basic.sh`:
  five unresolved groups. The former resolved edge was `. ./test-lib.sh` at line 21 in
  `t/t0000-basic.sh`; although `t/test-lib.sh` contains Git's test framework, the source argument
  does not establish that the script is launched from `t`.

All twelve samples retain identical navigation declarations. Two bats-core symlink blobs under
`pick_up_toplevel/folder1` and `folder2` were excluded according to their tracked Git mode, rather
than treated as executable source text.

### Working-directory comparison

The same selected files were processed sequentially with the implementation at
`1faac5c637a31e500bc09ef6ce9a63cff605051a` and the changed Bash resolver. These are edge groups for
the entire selected corpus, not only the twelve samples above.

| Repository | Resolved before | Ambiguous before | Unresolved before | Resolved after | Ambiguous after | Unresolved after |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Bash-it/bash-it | 0 | 0 | 97 | 0 | 0 | 97 |
| nvm-sh/nvm | 0 | 0 | 3 | 0 | 0 | 3 |
| bats-core/bats-core | 0 | 0 | 9 | 0 | 0 | 9 |
| git/git | 1132 | 0 | 343 | 0 | 0 | 1475 |

The 1132 former resolved groups have unknown runtime bases. Neither a unique selected match nor
two matches from the root and script directory exhausts possible working directories. They now
retain their literal arguments and source sites without publishing a target or guessed candidates.
Functions, source-site signatures, selected-file counts and extraction failures are unchanged.

A separate Bash 5.2.21 run on Ubuntu changed only the launch directory: the same `source ./lib.sh`
script loaded the root, script-directory and third-directory libraries in turn. Slashless
`source lib.sh` loaded a `PATH` library with `sourcepath` enabled, and the working-directory library
with it disabled. The same `./other.sh` invocation likewise ran different scripts from the root
and script directory. This checks shell semantics without executing corpus code.

`results/bash-working-directory.json` records the before/after counters and hashes for these four
Bash corpora and eighteen controls: the sixteen existing-language pins below plus Cats and Ox.
Every control edge hash and normalized file-facts/navigation hash is identical at the same selected
manifest. Real CLI/MCP calls also agree for all twelve Bash samples; every checked function name
is still accepted unchanged by `get_file`.

## Scala corpus

| Repository | Commit | Scala files | With facts | Checked cross-file edges | Confirmed | Unresolved sample groups | Ambiguous sample groups | Unexpected edges |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| typelevel/cats | f0c5f1b450b3250db4f6b819ebd9db4d583843bd | 835 | 835 | 8 | 8 | 113 | 1 | 0 |
| softwaremill/ox | 9cddca4fc4b9d0e71cd1b1aacfaba1aefc008d13 | 214 | 214 | 17 | 17 | 39 | 0 | 0 |

Cats samples: `core/src/main/scala/cats/Functor.scala`, `SemigroupK.scala`, `Show.scala`.
Ox samples: `core/src/main/scala/ox/Ox.scala`, `fork.scala`, `scheduling/RepeatConfig.scala`.
All cross-file resolved evidence in those samples was opened in source and target. Self-file
references are not related-file edges and are excluded from this checked count. These checks do not
certify every edge elsewhere in either corpus.

Ox exercises Scala 3 indented traits, classes, objects and top-level functions. Its checked targets
include `ErrorMode`, `ThreadHerd`, `ForkLocalMap`, `Supervisor`, `Ox`, `OxError`, `OxUnsupervised`,
`Schedule`, `ScheduleStop` and `ScheduledConfig`. Cats exercises braced code, selector imports,
higher-kinded types and multiple version-specific source layouts. The version-specific `Show`
candidate remains ambiguous rather than choosing one Scala version without build evidence.

`parity.mjs` ran all eighteen samples through the real CLI and MCP process. Canonical related-file
paths, statuses and reasons match. For each sample with a named function/member, search returned
its navigation name and `get_file` accepted that same symbol string. In particular, Scala 3 names
include `ox.fork` and `ox.scheduling.RepeatConfig.schedule`; no selector rewriting was used.

## Existing-language comparison

The scanner was built against both `af88c2bf47f9481858eda5d435c764183d4d1a1a` and the changed
implementation. Sixteen repositories were processed sequentially. The fifteen existing pins are
from `tools/DependencyLiveValidation/registry.json`; that registry has no Rust entry, so ripgrep
at `3fce3b5bb0236da2df6d99672afb8a719642eca7` supplies a separately pinned Rust control.
`results/existing-languages.json` records counts and hashes. Every edge hash and every normalized
file-facts/navigation hash is equal, not merely every aggregate counter.

| Repository | Edges before | Edges after |
| --- | ---: | ---: |
| curl | 10564 | 10564 |
| fmt | 6059 | 6059 |
| libuv | 4763 | 4763 |
| openssl | 27436 | 27436 |
| protobuf | 44551 | 44551 |
| serilog | 1723 | 1723 |
| client-golang | 1438 | 1438 |
| express | 453 | 453 |
| zod | 6696 | 6696 |
| react-hook-form | 1828 | 1828 |
| pydantic | 7043 | 7043 |
| spring-petclinic | 837 | 837 |
| okio | 4250 | 4250 |
| sinatra | 1149 | 1149 |
| guzzle | 1872 | 1872 |
| ripgrep | 2520 | 2520 |

## Deliberate boundaries

Bash does not evaluate expansions, interpreter flags, working-directory changes, computed
commands or function-call binding. Extensionless scripts are not recognized as Bash source by the
current extension bindings. Scala does not evaluate sbt/Mill settings, external artifacts, implicit
search or inherited-member binding. Wildcards, ambiguous source layouts, qualified type expressions
without proven import binding and unsupported local aliases remain explicit unresolved evidence.
More complex Scala 3 match/indent boundaries and platform-specific source sets have not received a
complete live corpus check. The checked samples are evidence of bounded correctness, not a global
claim about all shell or Scala constructs.
