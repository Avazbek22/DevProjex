# Release process

DevProjex has two local desktop release channels and five CI-owned channels.
`Scripts/release-all.ps1` owns the GitHub desktop artifacts and Microsoft Store
upload. AppImage, headless archives, and the Docker image are assembled only by
release workflows. NuGet and npm are built and published only by
`.github/workflows/publish-packages.yml`; the local desktop script never publishes
any CI-owned channel.

## Local channel model

The default invocation selects both local channels:

- `github`: six self-contained desktop artifacts for `win-x64`, `win-arm64`,
  `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`;
- `store`: one unsigned `.msixupload` whose bundle contains x64 and arm64
  application packages and all Store resource languages, plus the generated
  `.msixbundle` and x64 `.msix` companions used by local validation and WACK.

The build still runs in an isolated temporary workspace. Only validated outputs
are copied back to:

- `publish/github/v<version>`;
- `publish/store/v<version>`.

`Packaging/Headless/payload-manifest.json` keeps the cross-channel facts that are
independent of one build: the six RIDs, executable and grammar naming, and Store
alias, architectures, and resource languages. Every local build additionally
emits `publish-payload.<rid>.json`. This build receipt is the authoritative list
of publish files for that RID; each entry records its path, size, SHA-256, and,
for a managed assembly, its complete embedded-resource name table. The receipt
is covered by `SHA256SUMS.txt` and travels beside the release artifacts.

## Operator commands

Run a complete local release from an elevated PowerShell console because the
Store WACK phase requires administrator rights:

```powershell
./Scripts/release-all.ps1 -NonInteractive
```

Rebuild and validate only the Store channel, including WACK, from an elevated
PowerShell console:

```powershell
./Scripts/release-all.ps1 -Channels store -NonInteractive
```

Build one GitHub RID for debugging. This is intentionally marked `PARTIAL`, its
checksum manifest lists only the selected artifact and its RID receipt, and it
is not release-ready:

```powershell
./Scripts/release-all.ps1 -Channels github -Rids win-x64 -SkipWack -NonInteractive
```

`-GitHubArtifactsOnly` remains an alias for `-Channels github`:

```powershell
./Scripts/release-all.ps1 -Version 5.1 -GitHubArtifactsOnly
```

Build Store without WACK from a non-elevated console, then validate the existing
upload with WACK from an elevated console:

```powershell
./Scripts/release-all.ps1 -Channels store -SkipWack -NonInteractive
./Scripts/release-all.ps1 -Channels store -WackOnly -NonInteractive
```

Re-run the fail-closed static gate without building or invoking WACK:

```powershell
./Scripts/release-all.ps1 -ValidateArtifactsOnly -Channels github,store -NonInteractive
```

Validate only an existing partial GitHub RID set:

```powershell
./Scripts/release-all.ps1 -ValidateArtifactsOnly -Channels github -Rids win-x64 -NonInteractive
```

CI uses the same non-interactive channel selection and can invoke the standalone
gate explicitly:

```powershell
./Scripts/release-all.ps1 -Channels github -Rids win-x64,linux-x64 -SkipWack -NonInteractive
./Scripts/Test-ReleaseArtifacts.ps1 -PublishRoot publish -Version 5.1 -Channels github -Rids win-x64,linux-x64
./Scripts/Test-ReleaseArtifactGateMutation.ps1 -PublishRoot publish -Version 5.1 -Channels github -Rids win-x64,linux-x64
```

When `-NonInteractive` is set, `Read-Host` is never called. An invalid explicit
version or invalid channel/RID selection exits with code 1 and one diagnostic
line. Without `-Version`, the version still comes from `Directory.Build.props`
through `Scripts/release-helpers.ps1`.

## Release readiness

A complete GitHub channel must contain exactly the six named artifacts and a
matching `SHA256SUMS.txt`. A subset contains `PARTIAL-BUILD.txt`; validation may
pass for exactly the requested RIDs, but the summary remains `PARTIAL` and the set
must not be attached to a release.

The static gate checks, without running foreign-RID binaries:

- the exact selected GitHub artifact/receipt set and every SHA-256;
- Windows file/product versions, Linux and macOS version evidence, archive layout,
  and executable mode bits;
- the .NET single-file manifest against the RID receipt in both directions,
  including every file path, size, SHA-256, and every managed assembly resource;
  any compressed bundle entry fails validation;
- macOS `Info.plist` version and application layout;
- Store upload, bundle, x64/arm64 application packages, package/bundle versions,
  execution alias, exact application payload from both RID receipts, and all
  Store resource languages. The Store directory must contain exactly the
  versioned `.msixupload`, x64|arm64 `.msixbundle`, x64 `.msix`, and the two
  Windows RID receipts emitted by the existing packaging layout.

Store compares every application-package file and managed resource in both
directions. The only channel-generated additions are `AppxManifest.xml`,
`AppxBlockMap.xml`, `[Content_Types].xml`, `AppxSignature.p7x`, `resources.pri`,
and `Assets/**`; this allowlist is centralized in the validator. Sizes and
SHA-256 values are checked for archive/package entries. A missing or unexpected
help file, ignore profile, icon-pack asset, compression language resource,
gitleaks rule, notice, managed assembly, or native RID library fails identically.

The host `win-x64` artifact also runs `--version`, `tree`, compressed and full
`analyze`, the secret findings exit-code/redaction check, and the MCP initialize
handshake. The MCP check prints `SKIPPED` when Node is unavailable; no check is
silently omitted. Store is release-ready only after the static gate and WACK both
pass. A `-SkipWack` summary explicitly says that Store is not release-ready.

Every successful invocation prints one line per selected artifact with byte size
and SHA-256, the status of each selected channel, and its output directory.

## Adding a channel

A new distribution channel is not connected until all five conditions are met:

1. Its stable metadata is represented in the shared manifest and its build emits
   a complete content receipt from the channel's own publish inputs.
2. A static completeness gate fails closed on missing or invalid content.
3. A mutation gate proves the completeness gate rejects a damaged real artifact.
4. A functional smoke exercises the artifact through its public entry point.
5. Its publication path and release-readiness requirements are documented.

The current publishing paths remain separate: GitHub desktop archives and Store
uploads are prepared locally, AppImage is attached by the release workflow, and
NuGet/npm are published through `publish-packages.yml` after their own static,
mutation, and three-OS functional gates.

## CI-owned headless archives

`.github/workflows/package-headless.yml` builds all six
`DevProjex-headless.v<version>.<rid>.zip|tar.gz` archives from the same single-file
publish directories used to stage npm platform packages. The workflow validates
the complete set and uploads it only for a published release, or for a manual run
that names an existing `release_tag`. Outputs are staged below
`artifacts/headless/release/headless/v<version>`.

This channel satisfies the five connection requirements as follows:

1. RID and executable names come from `payload-manifest.json`; each build emits a
   complete `publish-payload.<rid>.json` from `_FilesToBundle`.
2. `Test-ReleaseArtifacts.ps1 -Channels headless` checks the exact archive set,
   USTAR executable modes, single-file entries, resources, sizes, and hashes.
3. `Test-ReleaseArtifactGateMutation.ps1 -Channels headless` damages deterministic
   file and resource entries plus grammar, localization, and native-library cases.
4. The workflow runs `--version`, `tree`, both `analyze` forms, the secret exit-3
   check, and an MCP initialize handshake against the linux-x64 archive payload.
5. The workflow attaches archives, receipts, and `SHA256SUMS.headless.txt` to the
   GitHub release; installation commands live in `Docs/Installation.md`.

The separate checksum filename is deliberate. Desktop artifacts and
`SHA256SUMS.txt` are produced by an operator, while headless assets are produced by
CI. Two independent producers cannot atomically update one checksum manifest.

## CI-owned Docker image

`.github/workflows/publish-container.yml` builds amd64 and arm64 from the root
`Dockerfile`, but pushes `ghcr.io/avazbek22/devprojex:<version>` and `latest` only
for `release: published`. Pull requests and manual runs build, validate, and smoke
without pushing. The SDK stage cross-publishes linux-arm64 on the x64 runner;
`BUILDPLATFORM` keeps the compiler native, and the final multi-architecture image
contains the prebuilt output without a target-architecture build step.

The container channel meets the same connection contract:

1. The folder publish opts into the shared build receipt and uses manifest RID and
   binary metadata. Store retains its existing automatic folder-receipt default.
2. `Test-ReleaseArtifacts.ps1 -Channels container` compares the extracted `/app`
   directory with its receipt in both directions, including managed resources.
3. `Test-ReleaseArtifactGateMutation.ps1 -Channels container` mutates a copied
   extracted payload and proves the common diff rejects it.
4. amd64 and native arm64 jobs run version, tree, compression, secret, and MCP
   smoke with `--read-only`, `--tmpfs /tmp`, and a read-only project mount.
5. The published multi-architecture manifest receives version and `latest` tags
   plus build-provenance attestation; Docker and MCP client recipes are documented
   in `Docs/Installation.md` and `Docs/McpServer.md`.

The image is a folder publish with `DevProjexGrammarDelivery=Content`. Grammar
libraries stay beside the executable, so neither .NET single-file extraction nor
grammar materialization needs a writable filesystem at startup.
