# Headless release channels

The implementation and workflow are ready before registry names are reserved.
Nothing in this document claims that the packages have already been published.
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
3. Create a short-lived granular npm access token that can publish exactly those
   seven packages.
4. Download the already gated artifacts from a successful dry run. Publish the six
   platform tarballs first with the granular token, then publish the `devprojex`
   launcher tarball. This bootstrap is required because npm trusted publishers can
   be configured only for existing packages.
5. On each of the seven npm packages, configure the GitHub Actions trusted publisher
   with repository `Avazbek22/DevProjex`, workflow filename
   `publish-packages.yml`, and environment `npm`.
6. Delete the granular npm access token and verify that no repository or environment
   secret contains it.
7. Reserve the NuGet package IDs `devprojex`, `devprojex.win-x64`,
   `devprojex.win-arm64`, `devprojex.linux-x64`, `devprojex.linux-arm64`,
   `devprojex.osx-x64`, and `devprojex.osx-arm64`.
8. In the NuGet account, configure trusted publishing for repository
   `Avazbek22/DevProjex`, workflow filename `publish-packages.yml`, environment
   `nuget`, and the seven package IDs.
9. Add the GitHub repository variable `NUGET_USER` with the NuGet account username;
   do not add a long-lived NuGet API-key secret.
10. Run **Publish Headless Packages** on the release branch with the intended
    `version`, `channels=both`, and `dry_run=true`. Confirm that build, static gate,
    mutation gate, and all three OS smoke jobs are green.
11. Re-run the same workflow and version with `channels=both` and `dry_run=false`.
    The workflow publishes six NuGet RID packages before the pointer, and six npm
    platform packages before the launcher; it does not use skip-duplicate behavior.

## Dry-run package sizes

The checked-in table below is refreshed from
`artifacts/headless/package-sizes.md` after the final local v5.2 dry run. It reports
compressed registry artifacts, not installed sizes.

| Package | Bytes | MiB |
|---|---:|---:|
| `devprojex-5.2.0.tgz` | 5,208 | 0.00 |
| `devprojex-cli-darwin-arm64-5.2.0.tgz` | 60,165,625 | 57.38 |
| `devprojex-cli-darwin-x64-5.2.0.tgz` | 63,410,007 | 60.47 |
| `devprojex-cli-linux-arm64-5.2.0.tgz` | 59,155,468 | 56.42 |
| `devprojex-cli-linux-x64-5.2.0.tgz` | 62,541,116 | 59.64 |
| `devprojex-cli-win32-arm64-5.2.0.tgz` | 62,294,400 | 59.41 |
| `devprojex-cli-win32-x64-5.2.0.tgz` | 65,464,085 | 62.43 |
| `devprojex.5.2.0.nupkg` | 29,914 | 0.03 |
| `devprojex.linux-arm64.5.2.0.nupkg` | 47,163,705 | 44.98 |
| `devprojex.linux-x64.5.2.0.nupkg` | 49,442,111 | 47.15 |
| `devprojex.osx-arm64.5.2.0.nupkg` | 46,156,060 | 44.02 |
| `devprojex.osx-x64.5.2.0.nupkg` | 48,302,367 | 46.06 |
| `devprojex.win-arm64.5.2.0.nupkg` | 48,405,056 | 46.16 |
| `devprojex.win-x64.5.2.0.nupkg` | 50,379,210 | 48.05 |
