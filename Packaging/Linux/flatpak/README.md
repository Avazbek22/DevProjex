# Flatpak packaging

This repository builds the sandboxed Linux channel; it does not submit an application
to Flathub or add files to GitHub releases. A separate Flathub repository and its first
publication are still required before `flatpak install flathub io.github.Avazbek22.DevProjex`
can succeed.

## Build

Use a native x86_64 or aarch64 Linux host with Flatpak and flatpak-builder. Install the
Flathub remote, then run from the repository root:

```bash
flatpak remote-add --user --if-not-exists flathub https://flathub.org/repo/flathub.flatpakrepo
bash Packaging/Linux/flatpak/prepare-source.sh
bash Packaging/Linux/flatpak/regenerate-nuget.sh
python3 Packaging/Linux/flatpak/generate-grammar-modules.py
flatpak-builder --user --install --force-clean flatpak-build \
  Packaging/Linux/flatpak/io.github.Avazbek22.DevProjex.yml
bash Packaging/Linux/flatpak/smoke.sh
```

`regenerate-nuget.sh` runs the official, commit-pinned
[flatpak-dotnet-generator](https://github.com/flatpak/flatpak-builder-tools/tree/master/dotnet)
under SDK 26.08 with dotnet10. The generated `nuget-sources.json` is a required local
build input, not an empty checked-in placeholder. Generate it on each native architecture;
the workflow does this for both x86_64 and aarch64, including the self-contained runtime
and native ReadyToRun compiler. Restore and publish inside flatpak-builder then use only
that local NuGet feed; no build-network permission is requested.

`prepare-source.sh` creates a checksum-pinned source archive from `HEAD`, not a
mutable `type: dir` source. Commit intended source changes before building. The
archive excludes all six prebuilt vendored-grammar directories and includes only
the application build inputs and Linux metadata. Its generated source descriptor
and archive are local inputs, not release assets or committed binaries.

The [stable 26.08 SDK image](https://github.com/flathub-infra/actions-images) and
[dotnet10 extension](https://github.com/flathub/org.freedesktop.Sdk.Extension.dotnet10)
provide the matching SDK. No experimental Avalonia Wayland backend is enabled.

## Delivery decisions

- Publish a self-contained directory to `/app/lib/devprojex`; `/app/bin/devprojex` is
  its apphost symlink. `DevProjexGrammarDelivery=Content` keeps the native grammar
  payload in `/app/lib/devprojex/grammars`, the existing content-delivery location.
  A single-file extraction step is neither necessary nor used.
- All four vendored grammar modules are derived from the shared source lock, including
  archive SHA-256, source files and compiler flags. The SDK compiler adds `-fPIC` for
  ELF shared libraries. The selected RID's vendored files are populated from these
  fresh builds before publishing; repository-provided Linux binaries are excluded.
- Git 2.56.0 is compiled from a checksum-pinned upstream archive. Existing Git process
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

`smoke.sh` is an initial installed-sandbox CLI and grammar-delivery check, not a
replacement for the desktop journey. On a real desktop additionally check:

1. Open a home project, use the tree and export, then close normally.
2. Connect a real `ModelContextProtocol.Client` process through `flatpak run`, and
   confirm that stdout contains only MCP messages and that the current folder survives.
3. Start the window and `mcp --live` with two separate `flatpak run` invocations;
   change checkboxes and confirm both server focus and window spotlight marks.
4. Install the managed shim and run it from the host shell.
5. Open an inaccessible path, apply its suggested user override, then reopen it.
6. Exercise Kotlin/TOML/XML/YAML compression and verify the resulting transformations,
   not just the presence of their libraries.
7. Check the Flathub update notice, absent update controls and environment diagnostics.

The source-level process tests substitute only the environment/file marker source;
they do not emulate Linux filesystem isolation or a desktop portal.
