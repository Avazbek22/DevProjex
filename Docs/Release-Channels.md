# Headless release channels

Both channels are live from v5.2: [`devprojex` on npm](https://www.npmjs.com/package/devprojex)
with six `@devprojex/cli-*` platform packages, and
[`devprojex` on NuGet](https://www.nuget.org/packages/devprojex) with six RID packages.
The server is listed in the MCP Registry as `io.github.Avazbek22/devprojex`.
The workflow always builds and gates both channels; `channels` only selects which
publish jobs run after the gates, and `dry_run` defaults to `true`.

## Flatpak desktop channel

The repository's [Flatpak build](../Packaging/Linux/flatpak/README.md) is separate
from npm, NuGet, MCP Registry publication and GitHub release assets. It targets
native x86_64 and aarch64 with Freedesktop 26.08 and dotnet10. A separate Flathub
repository and listing are required before public installation is available.
`flatpak-build.yml` is a read-only packaging gate and participates in the pinned
release candidate; it does not publish to Flathub or change `SHA256SUMS`.
The runtime differences apply only inside a detected Flatpak sandbox; see
[Installation](Installation.md#flatpak).

## Owner checklist for the first publication

Perform these steps in order:

1. Create or verify the npm owner account, enable two-factor authentication, and
   create the npm organization named `devprojex`.
2. Reserve the unscoped npm name `devprojex` and all six scoped names:
   `@devprojex/cli-win32-x64`, `@devprojex/cli-win32-arm64`,
   `@devprojex/cli-linux-x64`, `@devprojex/cli-linux-arm64`,
   `@devprojex/cli-darwin-x64`, and `@devprojex/cli-darwin-arm64`.
3. Download the already gated artifacts from a successful dry run. From a clean local
   directory, use `npm login` with two-factor authentication, publish the six platform
   tarballs first with `npm publish`, then publish the `devprojex` launcher tarball.
   This bootstrap is required because npm trusted publishers can be configured only
   for existing packages; it does not require an npm access token.
4. On each of the seven npm packages, configure the GitHub Actions trusted publisher
   with repository `Avazbek22/DevProjex`, workflow filename
   `publish-packages.yml`, and environment `npm`. In the same package settings,
   confirm that publishing access allows the trusted publisher to publish; the
   workflow runs `npm publish` under that identity and nothing else.
5. In the NuGet account, configure trusted publishing for repository
   `Avazbek22/DevProjex`, workflow filename `publish-packages.yml`, environment
   `nuget`, and the scope **push new packages and package versions** with the glob
   `devprojex*`. This policy creates the seven package IDs on their first workflow
   push, so no separate reservation is needed, and it keeps working for 5.2.1 and
   later; a scope limited to new packages or to new versions only blocks one of the two.
6. Add the GitHub repository variable `NUGET_USER` with the NuGet account username;
   do not add a long-lived NuGet API-key secret.
7. Run **Publish Headless Packages** on `master` with the intended `version`,
    `channels=both`, and `dry_run=true`. Confirm that build, static gate, mutation
    gate, and all three OS smoke jobs are green. The `npm` and `nuget` environments
    accept deployments from `master` only, so the publishing run in step 8 must
    start there too.
8. Re-run the same workflow and version with `dry_run=false`. For the first
    publication use `channels=nuget`: the npm packages of this version were already
    published by hand in step 3, and a rebuilt tarball is not guaranteed to be
    byte-identical, so `channels=both` would fail closed on the npm `dist.integrity`
    check after NuGet had already succeeded. From the next version on, both channels
    run in one dispatch with `channels=both`. The workflow publishes six NuGet RID packages before the pointer, and six npm
    platform packages before the launcher. A retry verifies an existing NuGet
    payload receipt or npm `dist.integrity` and skips only identical content; a
    mismatch fails closed and no package is overwritten.
9. Register the release in the [MCP Registry](https://registry.modelcontextprotocol.io)
    after both channels serve the new version. From the repository root, run
    `mcp-publisher login github` as the repository owner, then `mcp-publisher publish`.
    The registry reads `server.json`, confirms that the npm `devprojex` package
    declares the same `mcpName`, and that the NuGet README contains the matching
    `mcp-name:` line. Both are fixed inside published versions, so the static gate
    and the documentation contract tests check them before anything is published.

## Version bump

`server.json` carries the package version twice: the top-level `version` and each
package `version`. Update them together with `DevProjexVersion`; the contract tests
and the static package gate reject a manifest that differs from the packages.
`Packaging/Smithery/manifest.json` carries the same version and is updated with them;
its contract test fails until it matches.

## Glama listing

[Glama](https://glama.ai/mcp/servers/Avazbek22/DevProjex) builds its hosted server from the
Linux headless release archive. Its server page shows the README of the current head of
`master`, not of the commit pinned for the build, and refreshes it after every push. Glama
drops raw HTML, so the main README is plain Markdown: title, badges, the demo GIF, and
banners use Markdown syntax, and a contract test rejects HTML tags outside code. The card in
Glama search, the page description, and link previews use the Description field in the
Glama settings instead.

Glama pins only the current head of the default branch; an older commit is rejected with
"Commit not found" even when Glama already knows a newer one, and its copy of the repository
can lag GitHub by one to several hours. After each release, once Glama shows the current
`master` head, pin it and point the second build step at the new
`DevProjex-headless.v<version>.linux-x64.tar.gz` (the archive holds a single `devprojex`
file). Keep the start command `/opt/devprojex/devprojex mcp --root /app`, build, create the
Glama release with the package version, and press Sync.

Glama's Auto-Release can publish a release from a stale configuration on its own, and the
owner cannot delete or rename a release. Check the release list after every change.

## Smithery listing

[Smithery](https://smithery.ai) lists a local server as an MCPB bundle of at most 25 MB.
Every DevProjex platform build is larger than that, so the bundle carries no binary:
`Packaging/Smithery/manifest.json` starts `npx -y devprojex mcp --root <folder>`, and npx
installs the build for the user's platform. Smithery asks for the project folder when the
server is added. Its page shows the tools the manifest declares and does not run the server
to list them; a contract test keeps that list equal to the tools the server registers.

Publish the bundle after npm serves the same version. The manifest names that version, while
npx starts whatever version npm serves:

1. `pwsh Scripts/Build-SmitheryBundle.ps1` writes `artifacts/smithery/devprojex.mcpb`. It
   refuses a manifest whose version differs from `DevProjexVersion` or that npm does not serve
   yet, validates it with `@anthropic-ai/mcpb`, and checks that the archive holds only the
   manifest, the entry point, and the icon and stays under 25 MB. `-AllowUnpublishedVersion`
   checks the bundle before the npm release; that bundle is not for publication.
2. `npx -y @smithery/cli@4.11.1 auth login`, then
   `npx -y @smithery/cli@4.11.1 namespace show` to confirm the namespace.
3. `npx -y @smithery/cli@4.11.1 mcp publish artifacts/smithery/devprojex.mcpb -n <namespace>/devprojex`.

A client that adds DevProjex through Smithery gets an entry named `devprojex` that runs
`npx -y @smithery/cli@latest run <namespace>/devprojex`; the Smithery runner reads the saved
folder and starts the bundle command. That entry does not show the project folder, so
**Open in Claude Code**, **Open in Codex**, and `devprojex mcp connect` treat it as an entry
DevProjex did not write and change it only after **Replace** or `--replace`.

## Published 5.2.0 package sizes

Compressed registry artifacts as served by npm and nuget.org, not installed sizes.
nuget.org adds its repository signature, so its files are slightly larger than the
workflow artifacts.

| Package | Bytes | MiB |
|---|---:|---:|
| `devprojex-5.2.0.tgz` | 7,169 | 0.01 |
| `devprojex-cli-darwin-arm64-5.2.0.tgz` | 65,220,004 | 62.20 |
| `devprojex-cli-darwin-x64-5.2.0.tgz` | 68,789,374 | 65.60 |
| `devprojex-cli-linux-arm64-5.2.0.tgz` | 64,084,142 | 61.12 |
| `devprojex-cli-linux-x64-5.2.0.tgz` | 67,796,043 | 64.66 |
| `devprojex-cli-win32-arm64-5.2.0.tgz` | 67,378,450 | 64.26 |
| `devprojex-cli-win32-x64-5.2.0.tgz` | 70,814,774 | 67.53 |
| `devprojex.5.2.0.nupkg` | 45,933 | 0.04 |
| `devprojex.linux-arm64.5.2.0.nupkg` | 48,263,581 | 46.03 |
| `devprojex.linux-x64.5.2.0.nupkg` | 50,539,841 | 48.20 |
| `devprojex.osx-arm64.5.2.0.nupkg` | 47,249,233 | 45.06 |
| `devprojex.osx-x64.5.2.0.nupkg` | 49,394,338 | 47.11 |
| `devprojex.win-arm64.5.2.0.nupkg` | 49,571,039 | 47.27 |
| `devprojex.win-x64.5.2.0.nupkg` | 51,546,710 | 49.16 |
