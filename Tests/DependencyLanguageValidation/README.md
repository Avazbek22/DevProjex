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
