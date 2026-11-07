# Git process safety

Selecting a project directory gives DevProjex permission to read that directory. It does not give the repository permission to run programs from `.git/config`, hooks, attributes, aliases, transport helpers, or paging configuration. Starting with v5.2, every product-owned Git invocation is selected from a closed operation registry and receives one of three safety profiles.

This boundary applies equally to Desktop, Terminal Workspace, direct CLI commands, and MCP. DevProjex continues to parse `.gitignore`, repository-local `info/exclude`, and `.gitmodules` itself; those readers do not start Git.

## Threat model

| Repository mechanism | When Git would normally use it | DevProjex behavior |
|---|---|---|
| `core.fsmonitor` | Index refresh, `status`, `diff`, and `ls-files` | Sets `core.fsmonitor=false` for every profile. |
| Hooks and `core.hooksPath` | Checkout, worktree, fetch, and other lifecycle operations | Sets `core.hooksPath` to an application-owned empty directory. Clone also uses an application-owned empty template. |
| `filter.<name>.clean` or `.process` | Working-tree comparison and staging | Inspects effective repository configuration through `LocalRead`. The `changes` scope refuses the comparison with `DPX-GIT-UNSAFE-FILTER`; it never substitutes a raw comparison and calls it equivalent. |
| `filter.<name>.smudge`, clean, or process | Checkout and worktree materialization | Managed checkout overrides each safely discovered driver with an empty command and `required=false`. LFS and similar content stays in pointer form; DevProjex does not download it implicitly. |
| `diff.external`, `diff.<name>.command`, or `.textconv` | Diff generation | Uses both `--no-ext-diff` and `--no-textconv`. Configuration is inspected as defense-in-depth and no configured program is run. |
| `log.showSignature` and `gpg.program` | Log or signature rendering | Sets `log.showSignature=false`. No current registered read operation invokes `log`. |
| `core.pager` and pager environment | Output and some failures | Adds `--no-pager` globally and does not inherit pager environment variables. |
| Shell aliases | Alias expansion | The operation registry fixes a built-in subcommand. Same-named aliases cannot replace built-ins. |
| Credential helpers and askpass | Network authentication | Clears `credential.helper` and `core.askPass`. Interactive prompts are disabled. An authenticated URL may use only the temporary DevProjex askpass session. |
| `core.gitProxy`, `core.sshCommand`, HTTP proxy/header/cookie settings, upload-pack overrides | Network setup | Explicit network operations reject declared repository overrides and also clear the fixed HTTP/upload-pack keys on the command line. |
| `url.<base>.insteadOf` | URL rewrite before transport selection | Global configuration is isolated. A managed repository declaring a local rewrite is rejected before network access. Fetch receives the saved, verified URL directly rather than trusting `origin`. |
| Promisor remote and missing objects | Demand fetch during an apparently local read | Sets `GIT_NO_LAZY_FETCH=1`. A partial clone, declared by a promisor remote or by `extensions.partialClone`, is refused on Git older than 2.45, where that guarantee is unavailable. A missing object is an error, never an empty result. |
| `protocol.*`, remote helpers, and `ext` | Local or network transport dispatch | `LocalRead` and `ManagedCheckout` allow no protocol. `ExplicitNetwork` allows only the transport selected by a validated URL; `ext` and unknown helpers are never allowed. |
| `git`, `git.exe`, or `ssh` inside the project | Executable resolution through a relative/current-directory `PATH` entry | Resolves once from absolute `PATH` entries, rejects candidates in the current project tree or its parent directories, and pins the absolute path. |
| Inherited `GIT_*` variables | Repository, object, index, config, or namespace redirection | Builds a small environment allowlist, then removes repository/config injection variables as a second barrier. |
| Unbounded or stalled child process | Large output, helper hang, or descendant process | Applies a per-profile deadline, bounded stdout/stderr, cancellation, and whole-process-tree termination. Truncated output is never returned as a complete file list. |

## Common process boundary

Every registered operation begins with:

```text
git --no-pager --no-optional-locks
    -c core.fsmonitor=false
    -c core.quotepath=false
    -c core.hooksPath=<application empty-hooks directory>
    -c credential.helper=
    -c core.askPass=
    -c log.showSignature=false
    -c submodule.recurse=false
    -c core.attributesFile=<application empty-attributes file>
    -c core.excludesFile=<application empty-excludes file>
```

Windows also adds `-c core.longpaths=true`. Arguments are placed in `ProcessStartInfo.ArgumentList`; `UseShellExecute` is false and stdin, stdout, and stderr are redirected. The executable is an absolute path resolved once, not the token `git`.

The child environment starts with only the available values from this allowlist:

```text
PATH HOME USERPROFILE TEMP TMP SystemRoot LANG LC_ALL
```

`ExplicitNetwork` additionally inherits only `SSH_AUTH_SOCK` and the operator's
`HTTPS_PROXY`, `HTTP_PROXY`, `NO_PROXY`, `SSL_CERT_FILE`, `SSL_CERT_DIR`, and
`GIT_SSL_CAINFO` values. These values come from the DevProjex process environment,
not repository configuration. Repository-selection variables such as `GIT_DIR`,
`GIT_WORK_TREE`, `GIT_INDEX_FILE`, object-directory variables, namespaces,
discovery overrides, and external `GIT_CONFIG_*` injection are removed. DevProjex
then adds its own profile variables. A password, when explicitly supplied for a
remote source, exists only in the temporary askpass environment and never in argv
or a persisted remote URL. The same in-memory askpass session is reused for
explicit branch listing and update operations for that source and is disposed with
the Git service.

## LocalRead

`LocalRead` is used for a user-selected project and never permits network access or repository-provided execution. In addition to the common arguments it adds:

```text
-c protocol.allow=never
```

Its Git environment is:

```text
GIT_CONFIG_NOSYSTEM=1
GIT_CONFIG_GLOBAL=<application empty file>
GIT_ATTR_NOSYSTEM=1
GIT_TERMINAL_PROMPT=0
GIT_OPTIONAL_LOCKS=0
GIT_NO_LAZY_FETCH=1
GIT_ALLOW_PROTOCOL=
GIT_PROTOCOL_FROM_USER=0
GIT_ASKPASS=
SSH_ASKPASS=
SSH_ASKPASS_REQUIRE=never
GCM_INTERACTIVE=Never
GCM_GUI_PROMPT=false
```

Local reads have a 30-second deadline. File-list callers additionally enforce retained-output and per-path limits. These are the pinned command forms:

```text
ls-files --cached --full-name -z --
ls-files --others --exclude-standard -z --
diff --no-ext-diff --no-textconv --name-status -z --cached --
diff --no-ext-diff --no-textconv --name-status -z --
diff --no-ext-diff --no-textconv --name-status -z <validated-range> --
rev-parse --verify --quiet --end-of-options <validated-ref>^{commit}
config --get remote.origin.url
config --show-scope --type=bool --get-regexp <fixed-path-semantics-pattern>
config --name-only --get-regexp <fixed-safety-pattern>
config -z --get-regexp ^core\.(autocrlf|eol)$
config --system -z --get-regexp ^core\.(autocrlf|eol)$
config --file <user-configuration-file> -z --get-regexp ^core\.(autocrlf|eol)$
branch
branch -r
rev-parse --abbrev-ref HEAD
symbolic-ref refs/remotes/origin/HEAD
worktree list --porcelain
log --topo-order --no-renames --no-decorate --format=%x1e%H%x00 --name-only -z -n 200 --
rev-parse --is-shallow-repository
--literal-pathspecs ls-tree -r -z --full-tree <validated-commit-id> -- <selected-paths>
--literal-pathspecs ls-files --stage -z -- <selected-paths>
cat-file --batch-check
cat-file --batch
```

The last four read committed and staged file versions for a Git-scoped diff. A revision is first
resolved to a commit id with the `rev-parse` form above, and only that full hexadecimal id reaches
`ls-tree`. The paths come from the call's effective selection, after `--` and with
`--literal-pathspecs`, so a leading `-` or pathspec magic such as `:(glob)` is an ordinary file name;
a path with a control character, an empty, `.` or `..` segment, or a backslash is refused before Git
starts, and one path list is limited to 24,000 characters. Only on Windows does a selected path's `\`
become the `/` Git separates directories with; on Linux and macOS a backslash is part of a file name,
so a file such as `config\.env` is listed as unsupported without being read, never as the old side of
`config/.env`. Both `cat-file` forms receive only full
lowercase hexadecimal object ids on standard input, one per line, so Git's revision-name resolution
never sees caller text. Without `--textconv` or `--filters`, `cat-file` returns stored bytes and runs
no repository program; none of these forms compares against the working tree, so no clean, smudge,
process or external-diff driver can run. One object is read only when it is at most 16 MiB, and one
call reads at most 64 MiB of object content.

The working-tree comparison of the `changes` scope is the one read that also honors user
configuration, and only for line endings. Git for Windows keeps `core.autocrlf` in its system
configuration, which isolation skips; without it a CRLF checkout compares as modified whenever Git
rechecks file content, as it does after a fresh clone or a copy, and the scope would select nearly
every file. Before that comparison DevProjex reads `core.autocrlf` and `core.eol` from the system
file (unless `GIT_CONFIG_NOSYSTEM` is set for DevProjex), the user's files (`GIT_CONFIG_GLOBAL`, or
the XDG file and then `~/.gitconfig`), and the repository, in Git's own precedence. It passes only
validated values to that one `diff` as `-c core.autocrlf=true|false|input` and
`-c core.eol=lf|crlf|native`. Any other value is ignored, no other setting from those files reaches
Git, and an unreadable repository scope leaves the comparison without these settings.

The fixed safety query detects filter, external-diff, transport-override, and promisor keys. A
partial clone is recognized by either key Git itself honors, a `remote.<name>.promisor` setting or
`extensions.partialClone`, with the pinned query
`config --name-only --get-regexp ^(remote\..*\.promisor|extensions\.partialclone)$`. DevProjex does not use a fictional `GIT_CONFIG_LOCAL` switch and does not write `safe.directory`, the user's global config, or `.git/config` to obtain isolation.

## ManagedCheckout

`ManagedCheckout` is restricted to a repository container marked as owned by the DevProjex cache. The target and any worktree path must stay inside that container, and an in-process cache lease must be active for the container. It uses the common environment, allows no transport, keeps `GIT_NO_LAZY_FETCH=1`, and has a two-minute deadline.

Before materializing files, DevProjex reads the effective filter declarations with `LocalRead`. For every validated driver name it adds exact overrides:

```text
-c filter.<name>.clean=
-c filter.<name>.smudge=
-c filter.<name>.process=
-c filter.<name>.required=false
```

There is no wildcard filter override in Git. If the filter registry cannot be read or a driver name cannot be represented safely, checkout fails closed. The registered forms are detached checkout, branch checkout/reset, hard reset, worktree add/remove/prune, and the fixed `extensions.worktreeConfig` / `devprojex.branch` tracking writes. Missing objects do not trigger a fetch. Consequently, an LFS pointer or another smudge-managed file remains a pointer until a future explicit feature provides a reviewed download path; the diagnostic trace names the disabled drivers instead of making that substitution silent.

Git driver subsection names are case-sensitive, so DevProjex preserves and
disables `filter.Foo` and `filter.foo` independently; section names and property
names remain case-insensitive. For the fixed `git config --get-regexp` safety
query, exit code 0 is parsed and exit code 1 with empty output means no matches.
Any other exit code, missing process result, or output-limit breach makes the
inspection unavailable, and managed checkout and change-scope requests fail
closed without including Git output in the user-facing diagnostic.

## ExplicitNetwork

`ExplicitNetwork` is used only after validating a remote source and deciding that network access is part of the requested operation. Clone is fixed to:

```text
clone --no-checkout --no-recurse-submodules \
  --template=<application empty-template directory> \
  --depth 1 --progress <validated-url> <validated-target>
```

Checkout is a later `ManagedCheckout` operation after the clone is published into the application cache. Fetch is fixed to:

```text
fetch --no-recurse-submodules --no-auto-maintenance --no-tags \
  --depth <validated-depth> <saved-url> \
  +refs/heads/<validated-branch>:refs/remotes/origin/<validated-branch>

fetch --no-recurse-submodules --no-auto-maintenance --quiet --no-tags \
  --deepen <validated-depth> <saved-url> [<validated-refspec>]
```

Online branch listing uses `ls-remote --heads <saved-url>`. Before fetch or `ls-remote`, the mutable `remote.origin.url` must match the identity saved when DevProjex cloned the repository, and the safety query must find no local `insteadOf`, proxy, SSH-command, HTTP header/cookie, or upload-pack override. Fetch never resolves the remote by the name `origin`.

HTTPS allows only `https`; SSH/SCP sources allow only `ssh`. The v5.2 product has no explicit opt-in surface for `http`, `git`, or `file`, so those transports are rejected. `ext` and unknown remote helpers are always rejected. SSH uses one absolute executable selected from safe `PATH` entries and retains non-interactive `BatchMode=yes`. Network operations have a ten-minute deadline. Credential helpers remain disabled; the existing temporary DevProjex askpass flow is the only password path.

Clone and fetch also share the repository-cache resource policy. DevProjex checks
free space on the destination filesystem before clone, monitors the application-owned
staging or cache directory while Git runs, and terminates the complete process tree
with `DPX-GIT-CACHE-QUOTA` if the configured cache-size limit is exceeded. A transfer
rejected before launch because the destination filesystem lacks the required reserve
reports `DPX-GIT-CACHE-RESERVE`; the diagnostic does not disclose filesystem sizes.

GitHub ZIP fallback applies a separate no-progress deadline while reading the
response body as well as compressed and extracted byte limits. It reports the ref
that was actually downloaded. Unix symbolic-link entries are skipped with a counted
`DPX-ZIP-SYMLINK-SKIPPED` diagnostic instead of being materialized as regular files.

## Operation registry

| Operation | Profile | Pinned purpose |
|---|---|---|
| `ReadTrackedIndex` | LocalRead | Read cached tracked paths. |
| `ReadStagedChanges` | LocalRead | Read index-to-HEAD name/status. |
| `ReadWorkingChanges` | LocalRead | Read working-tree name/status after filter inspection. |
| `ReadUntracked` | LocalRead | Read non-ignored untracked paths. |
| `ResolveCommit` | LocalRead | Resolve one validated revision without option injection. |
| `ReadRefDiff` | LocalRead | Read a validated two-ref diff. |
| `ReadConfigValue` | LocalRead | Run one of the fixed config queries. |
| `ReadRemoteUrl` | LocalRead | Read `remote.origin.url` without contacting it. |
| `ListBranches` | LocalRead or ExplicitNetwork | Read local/cached branch data, or explicitly list verified remote heads. |
| `CloneRepository` | ExplicitNetwork | Create a shallow no-checkout clone from a validated URL. |
| `FetchBranch`, `FetchDeepen` | ExplicitNetwork | Fetch from the saved URL with fixed recursion/maintenance restrictions. |
| `ManagedCheckout` | ManagedCheckout | Materialize/reset only a leased application cache. |
| `ManagedWorktreeAdd`, `ManagedWorktreeRemove`, `ManagedWorktreePrune` | ManagedCheckout | Manage leased cache worktrees. |
| `ManagedWorktreeList` | LocalRead | Probe/list cache worktrees without writing. |
| `ManagedConfigWrite` | ManagedCheckout | Write only fixed DevProjex cache metadata keys or the fixed tracked-branch setting. |
| `ReadHistoryWindow` | LocalRead | Read changed paths of the last 200 commits for importance ranking. |
| `ReadShallowRepositoryState` | LocalRead | Report whether the repository is a shallow clone. |
| `ReadTreeEntries` | LocalRead | List blob entries of one resolved commit for literal selected paths. |
| `ReadIndexEntries` | LocalRead | List index entries for literal selected paths. |
| `ReadObjectHeaders` | LocalRead | Report type and size for validated object ids read from standard input. |
| `ReadObjectContents` | LocalRead | Stream stored bytes for validated object ids read from standard input. |

No MCP argument, repository value, profile file, or command-line token selects a Git subcommand. Callers supply only the validated operands accepted by a registered operation.

## Compatibility and tradeoffs

- Full no-demand-fetch guarantees require Git 2.45 or newer. Older versions ignore `GIT_NO_LAZY_FETCH`; ordinary repositories continue to work, but DevProjex refuses LocalRead for a partial clone with a promisor remote.
- Disabling fsmonitor can make reads slower in very large working trees. It avoids executing an untrusted monitor and does not change the selected file set.
- Isolating system/global config intentionally removes user `insteadOf`, exclude,
  and credential-helper behavior. Line-ending settings are the single exception, read only for
  the `changes` working-tree comparison as described under LocalRead. Explicit network operations retain only the
  operator environment proxy and certificate variables listed above; repository
  proxy, TLS, header, cookie, and transport overrides remain rejected.
- Disabling smudge/clean/process during managed materialization leaves LFS and similar pointer files unchanged. No background download is attempted.
- The `changes` scope cannot reproduce Git's exact working comparison without a configured clean/process filter. It fails with `DPX-GIT-UNSAFE-FILTER` and names the driver. Staged and ref-to-ref comparisons remain available because they do not require that working-tree conversion.

## Known limitations

Repository configuration belongs to another process. A hostile process can replace `.git/config` between DevProjex's fixed safety query and the following Git process. Each process still receives the execution, protocol, environment, and hook restrictions above, but the preflight decision and invocation are not an atomic filesystem transaction.

For SSH, DevProjex pins the SSH executable and prevents repository `core.sshCommand` from selecting another program. The user's SSH configuration can still contain `ProxyCommand` or `Match exec`; `BatchMode=yes` does not disable those directives. Fully isolating or explicitly selecting SSH configuration is intentionally left for a future, separately reviewed change.
