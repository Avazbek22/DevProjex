# Microsoft Store screenshots

Store screenshots are generated from the published `win-x64` single-file application in an interactive Windows session. The capture includes the native title bar, window shadow, acrylic background and a controlled amount of desktop around the window.

Run from the repository root:

```powershell
.\Scripts\generate-store-screenshots.ps1
```

The script:

- uses the newest Partner Center `listingData-*.csv` export, or the file passed with `-PartnerCenterCsv`;
- captures every current `Assets/Localization` language and maps the Partner Center locale columns to those assets;
- publishes the same ReadyToRun single-file shape used by release validation;
- captures the five scenes declared in `store-screenshots.json` from a clean read-only snapshot of `HEAD`;
- isolates application settings and recent-workspace state in a temporary directory;
- writes screenshots and `listingData.csv` under `ImportFolder`;
- produces `artifacts/store-screenshots/contact-sheet.png` for visual review;
- runs the existing Store listing validator.

Use `-PublishedExe <path> -SkipPublish` to capture with an already published `DevProjex.exe`. The capture protocol waits for observable UI state and rendered composition frames, then allows DWM a short final presentation guard before reading desktop pixels. It does not depend on project-load timing instrumentation, which is disabled in release binaries.

Requirements: Windows 11 interactive desktop, primary working area of at least 2048×1280, hidden desktop icons, and no overlapping always-on-top windows. Review the contact sheet before importing `listingData.csv` into Partner Center.
