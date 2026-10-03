# Headless release channels

Both channels are live from v5.2: [`devprojex` on npm](https://www.npmjs.com/package/devprojex)
with six `@devprojex/cli-*` platform packages, and
[`devprojex` on NuGet](https://www.nuget.org/packages/devprojex) with six RID packages.
The server is listed in the MCP Registry as `io.github.Avazbek22/devprojex`.
The workflow always builds and gates both channels; `channels` only selects which
publish jobs run after the gates, and `dry_run` defaults to `true`.

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

## Glama listing

[Glama](https://glama.ai/mcp/servers/Avazbek22/DevProjex) builds its hosted server from the
Linux headless release archive and shows the README of the commit pinned in its settings.
Glama drops raw HTML from a README, so the listing uses its own Markdown-only, MCP-first
README in `Packaging/Glama/README.md`; the contract tests parse its commands and check its
tool table against the catalog.

After each release:

1. Update `Packaging/Glama/README.md` if tools, flags, or launch commands changed.
2. On an up-to-date `master`, run `./Scripts/New-GlamaReadmeSnapshot.ps1 -Push`. It pushes a
   commit with the Glama README in place of `README.md` and a second commit that restores
   the main README, then prints the snapshot SHA.
3. In the Glama server settings, pin the snapshot SHA. Glama's copy of the repository can lag
   GitHub by an hour or two; "Commit not found" means waiting, not a wrong SHA.
4. Point the second build step at the new `DevProjex-headless.v<version>.linux-x64.tar.gz`
   (the archive holds a single `devprojex` file), keep the start command
   `/opt/devprojex/devprojex mcp --root /app`, build, create the Glama release with the
   package version, and press Sync. The listing should show every catalog tool.

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
