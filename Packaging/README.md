# Packaging

Desktop GitHub and Store artifacts remain owned by `Scripts/release-all.ps1`.
Channel selection, partial RID builds, static validation, and the separate WACK
workflow are documented in [`Docs/Release-Process.md`](../Docs/Release-Process.md).
`-GitHubArtifactsOnly` remains a supported alias for `-Channels github`.
Headless NuGet/npm artifacts are built by `Scripts/build-headless-packages.ps1`
and published only by `.github/workflows/publish-packages.yml` after static and
three-OS functional gates pass.

```powershell
./Scripts/build-headless-packages.ps1 -Version 5.2 -DryRun
./Scripts/Test-HeadlessPackages.ps1 -ArtifactsRoot artifacts/headless -Version 5.2.0
./Scripts/Test-HeadlessPackageGateMutation.ps1 -ArtifactsRoot artifacts/headless -Version 5.2.0
```

The expected RID and grammar payload is centralized in
`Packaging/Headless/payload-manifest.json`. NuGet contains a pointer plus six RID
packages; npm contains six platform packages plus the dependency-free launcher.
No channel is published by the build script.

Examples for the local desktop channels:

```powershell
./Scripts/release-all.ps1 -Channels github -Rids win-x64 -SkipWack -NonInteractive
./Scripts/release-all.ps1 -Channels store -SkipWack -NonInteractive
./Scripts/release-all.ps1 -Channels store -WackOnly -NonInteractive
./Scripts/release-all.ps1 -ValidateArtifactsOnly -Channels github,store -NonInteractive
```

The owner-only registration and first-publish checklist is maintained in
[`Docs/Release-Channels.md`](../Docs/Release-Channels.md).
