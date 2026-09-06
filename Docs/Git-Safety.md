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
| Promisor remote and missing objects | Demand fetch during an apparently local read | Sets `GIT_NO_LAZY_FETCH=1`. A partial clone is refused on Git older than 2.45, where that guarantee is unavailable. A missing object is an error, never an empty result. |
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

`SSH_AUTH_SOCK` is added only for `ExplicitNetwork`. Repository-selection variables such as `GIT_DIR`, `GIT_WORK_TREE`, `GIT_INDEX_FILE`, object-directory variables, namespaces, discovery overrides, and external `GIT_CONFIG_*` injection are removed. DevProjex then adds its own profile variables. A password, when explicitly supplied for a remote source, exists only in the temporary askpass environment and never in argv or a persisted remote URL.

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
branch
branch -r
rev-parse --abbrev-ref HEAD
symbolic-ref refs/remotes/origin/HEAD
worktree list --porcelain
```

The fixed safety query detects filter, external-diff, transport-override, and promisor keys. DevProjex does not use a fictional `GIT_CONFIG_LOCAL` switch and does not write `safe.directory`, the user's global config, or `.git/config` to obtain isolation.

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

No MCP argument, repository value, profile file, or command-line token selects a Git subcommand. Callers supply only the validated operands accepted by a registered operation.

## Compatibility and tradeoffs

- Full no-demand-fetch guarantees require Git 2.45 or newer. Older versions ignore `GIT_NO_LAZY_FETCH`; ordinary repositories continue to work, but DevProjex refuses LocalRead for a partial clone with a promisor remote.
- Disabling fsmonitor can make reads slower in very large working trees. It avoids executing an untrusted monitor and does not change the selected file set.
- Isolating system/global config intentionally removes user `insteadOf`, proxy, exclude, and credential-helper behavior. Explicit credentials still use the DevProjex askpass session.
- Disabling smudge/clean/process during managed materialization leaves LFS and similar pointer files unchanged. No background download is attempted.
- The `changes` scope cannot reproduce Git's exact working comparison without a configured clean/process filter. It fails with `DPX-GIT-UNSAFE-FILTER` and names the driver. Staged and ref-to-ref comparisons remain available because they do not require that working-tree conversion.

## Known limitations

Repository configuration belongs to another process. A hostile process can replace `.git/config` between DevProjex's fixed safety query and the following Git process. Each process still receives the execution, protocol, environment, and hook restrictions above, but the preflight decision and invocation are not an atomic filesystem transaction.

For SSH, DevProjex pins the SSH executable and prevents repository `core.sshCommand` from selecting another program. The user's SSH configuration can still contain `ProxyCommand` or `Match exec`; `BatchMode=yes` does not disable those directives. Fully isolating or explicitly selecting SSH configuration is intentionally left for a future, separately reviewed change.
