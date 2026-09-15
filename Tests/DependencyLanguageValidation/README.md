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
test scripts. Without Git, 96/104 sources are computed (92.31%); literal path resolution has low
coverage in Bash-it and bats-core. Absolute literal paths remain unresolved, so the literal count
does not imply a resolved project dependency.

Three files per repository were inspected:

- Bash-it: `bash_it.sh`, `completion/available/system.completion.bash`,
  `plugins/available/rbenv.plugin.bash`: no resolved edges, 17 unresolved groups.
- nvm: `nvm.sh`, `install.sh`, `test/common.sh`: no resolved edges, 3 unresolved groups.
- bats-core: `contrib/release.sh`, `lib/bats-core/test_functions.bash`,
  `lib/bats-core/tracing.bash`: no resolved edges, 6 unresolved groups.
- Git: `ci/run-build-and-tests.sh`, `ci/test-documentation.sh`, `t/t0000-basic.sh`:
  one resolved edge, four unresolved groups. The resolved edge is `. ./test-lib.sh` at line 21 in
  `t/t0000-basic.sh`; the target `t/test-lib.sh` contains Git's test framework.

Both source and target of that edge were opened. No unexpected resolved edge remains in these
samples. Two bats-core symlink blobs under `pick_up_toplevel/folder1` and `folder2` were excluded
according to their tracked Git mode, rather than treated as executable source text.

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
