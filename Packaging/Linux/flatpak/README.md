# Flatpak packaging

This repository builds the sandboxed Linux channel; it does not submit an application
to Flathub or add files to GitHub releases. A separate Flathub repository and its first
publication are still required before `flatpak install flathub io.github.Avazbek22.DevProjex`
can succeed.

## Build

Use a native x86_64 or aarch64 Linux host with Flatpak. Install the
Flathub remote, then run from the repository root:

```bash
flatpak remote-add --user --if-not-exists flathub https://flathub.org/repo/flathub.flatpakrepo
flatpak install --user --noninteractive flathub org.flatpak.Builder
bash Packaging/Linux/flatpak/prepare-source.sh
bash Packaging/Linux/flatpak/regenerate-nuget.sh
python3 Packaging/Linux/flatpak/generate-grammar-modules.py
flatpak run org.flatpak.Builder --user --install --install-deps-from=flathub \
  --force-clean --disable-rofiles-fuse --jobs=2 --repo=repo \
  --compose-url-policy=full --mirror-screenshots-url=https://dl.flathub.org/media flatpak-build \
  Packaging/Linux/flatpak/io.github.Avazbek22.DevProjex.yml
python3 Packaging/Linux/flatpak/check-lint.py manifest Packaging/Linux/flatpak/io.github.Avazbek22.DevProjex.yml
python3 Packaging/Linux/flatpak/check-lint.py builddir flatpak-build
python3 Packaging/Linux/flatpak/check-lint.py repo repo
python3 -m venv /tmp/devprojex-flatpak-check-python
/tmp/devprojex-flatpak-check-python/bin/pip install -r Packaging/Linux/flatpak/smoke-requirements.txt
PATH="/tmp/devprojex-flatpak-check-python/bin:$PATH" \
  xvfb-run -a -s '-screen 0 1920x1080x24' bash Packaging/Linux/flatpak/smoke.sh
```

`regenerate-nuget.sh` runs the official, commit-pinned
[flatpak-dotnet-generator](https://github.com/flatpak/flatpak-builder-tools/tree/master/dotnet)
under SDK 26.08 with dotnet10. The generated `nuget-sources.json` is a required local
build input, not an empty checked-in placeholder. Generate it on each native architecture;
the workflow does this for both x86_64 and aarch64, including the self-contained runtime
and native ReadyToRun compiler. Cross-generation is also supported:
`regenerate-nuget.sh aarch64 Packaging/Linux/flatpak/nuget-sources-arm64.json`.
Both SDK host-RID properties are set during restore so that the descriptor includes
ARM64 crossgen2 even when generated on x86_64. This does not execute ARM64 code or
replace a native ARM64 build. Restore and publish inside flatpak-builder then use only
that local NuGet feed; no build-network permission is requested.

`prepare-source.sh` creates a checksum-pinned source archive from `HEAD`, not a
mutable `type: dir` source. Commit intended source changes before building. The
archive excludes all six prebuilt vendored-grammar directories and includes only
the application build inputs and Linux metadata. Its generated source descriptor
and archive are local inputs, not release assets or committed binaries. In this
repository CI generates the descriptors from each commit to avoid stale inputs.
A future standalone Flathub repository must commit its generated NuGet descriptor
beside the manifest, along with a checksum-pinned application source descriptor.
Do not edit generated descriptors by hand.

The [stable 26.08 SDK image](https://github.com/flathub-infra/actions-images) and
[dotnet10 extension](https://github.com/flathub/org.freedesktop.Sdk.Extension.dotnet10)
provide the matching SDK. No experimental Avalonia Wayland backend is enabled.

## Delivery decisions

- Publish a self-contained directory to `/app/lib/devprojex`; `/app/bin/devprojex` is
  a two-line executable shell launcher into that directory. `DevProjexGrammarDelivery=Content` keeps the native grammar
  payload in `/app/lib/devprojex/grammars`, the existing content-delivery location.
  A single-file extraction step is neither necessary nor used.
- All four vendored grammar modules are derived from the shared source lock, including
  archive format and SHA-256, source files, required TOML/YAML source patches and compiler flags. The SDK compiler adds `-fPIC` for
  ELF shared libraries. The selected RID's vendored files are populated from these
  fresh builds before publishing; repository-provided Linux binaries are excluded.
- Git 2.56.0 is compiled from a checksum-pinned upstream archive using its supported
  `NO_RUST=YesPlease` option, without optional Rust dependencies. Existing Git process
  isolation is unchanged: availability of `~/.gitconfig` does not enable hooks,
  credential helpers or untrusted repository configuration blocked by Git-Safety.md.
- Permissions expose home, X11, shared memory, GPU rendering and network. Portals have
  their default D-Bus access; Flatpak supplies the accessibility bus address. The only
  additional bus name supports Avalonia's global menu service, as noted in the
  manifest. No host spawning.
- Only the installed desktop-entry copy gets the app-id WM_CLASS. Shared AppImage
  metadata stays unchanged.
- Documents portal `GetHostPaths` requires interface version 5. The existing
  `Tmds.DBus.Protocol` 0.94.1 dependency is reused, not a second D-Bus stack.
  Older or unavailable portals retain the document path for display and refuse to
  emit a host client command for it. The complete D-Bus query has a bounded deadline.
- Live profiles and journals use application XDG state. Desktop control uses
  `$XDG_RUNTIME_DIR/app/$FLATPAK_ID`; overlong socket paths never fall back to a
  per-launch `/tmp` inside Flatpak. Other channels retain their previous paths.
- Separate launches have private PID namespaces. Flatpak live-session records use
  unique identifiers shared with journal headers and the existing bounded heartbeat,
  not cross-namespace PID probing. Concurrent starts cannot overwrite a journal merely
  because their namespace PIDs and start seconds match. Desktop discovery probes its
  owned shared socket instead. Ordinary
  channels retain PID-based names, process checks and their serialized record shape.

## Runtime checks before publication

The requested `--filesystem=home` permission requires Flathub's
[`finish-args-home-filesystem-access` exception](https://docs.flathub.org/docs/for-app-authors/linter#finish-args-home-filesystem-access).
It is not approved by a comment in this manifest. The owner must arrange that
approval before the strict linter can accept this permission. This repository
does not suppress the error or submit an exception request on the owner's behalf.
The permission supports non-interactive CLI/MCP roots, project-local client
configuration and the host terminal shim; removing it changes the requested sandbox
workflow and requires a separate product decision.

`smoke.sh` checks an installed application through real consumers, not an emulated
Flatpak environment. It requires .NET SDK 10.0.400 or later in the allowed feature
band, Python with the pinned check requirements, X11 (WSLg or Xvfb), and the host
`gdbus` tool for the optional Documents portal check. Set `DOTNET` to an explicit
SDK executable if needed. An optional first argument selects the evidence folder.
The workflow runs the same script on both native architectures under Xvfb.

1. Open a home project, check its real desktop-control socket, export context to
   the clipboard, capture native tree/preview pixels, then close normally.
2. Connect a real `ModelContextProtocol.Client` process through `flatpak run`, and
   confirm that stdout contains only MCP messages and that the current folder survives.
3. Start the window and `mcp --live` with two separate `flatpak run` invocations;
   change selection through the PTY, confirm server focus and the TUI's existing
   `A` activity marker, normal registration cleanup and stale-heartbeat removal.
   The GUI check also captures its returned `✦` marker during a live SDK call.
4. Enable the managed shim through the window and run it from the host shell.
   A disposable self-contained check client calls the same setup service outside
   and inside Flatpak to check ordinary/Flatpak/ordinary migration byte-for-byte.
5. Check inaccessible GUI, CLI, MCP and PTY roots, apply the suggested user override,
   reopen the folder in a real window and reset access. An empty mount-point directory can remain
   in Flatpak's private `/var/tmp` after reset; the check verifies that its real
   host file is no longer readable, not that the mount-point name disappears.
6. Exercise Kotlin/TOML/XML/YAML compression and verify the resulting transformations,
   not just the presence of their libraries.
7. Check the Flathub update notice, absent update controls and environment diagnostics.

The checks refuse pre-existing application overrides rather than replacing someone
else's permissions. Only their temporary override is reset. Fixtures and the
self-contained check client are removed; evidence remains in `flatpak-smoke-evidence/`
by default. Source archives and descriptors remain ignored build inputs. All linter
findings are printed: `check-lint.py` permits only the explicitly pending home-access
finding and rejects every other error or warning. AppStream composition needs full
media URLs and screenshot mirroring, including its OSTree screenshots ref.

The Documents check records the actual interface version and exports a real document
when the service is available. Interface version 4 cannot prove successful v5 host-path
mapping; this is recorded as a fallback check, not a successful `GetHostPaths` call.
External-link and file-manager behavior depends on registered desktop handlers;
their invocation must not close the workspace. Native folder picking and successful
v5 mapping still need a desktop that provides those services.

The source-level process tests substitute only the environment/file marker source;
they do not emulate Linux filesystem isolation or a desktop portal.
