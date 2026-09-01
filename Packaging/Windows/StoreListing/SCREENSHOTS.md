# Microsoft Store media

Store media is generated in an interactive Windows session. GUI screenshots use the published `win-x64` single-file application and include the native title bar, window shadow, acrylic background, and a controlled amount of desktop around the window. TUI screenshots use a dedicated capture host inside an isolated Windows Terminal window and crop away all terminal chrome.

Run from the repository root:

```powershell
.\Scripts\generate-store-media.ps1 `
  -PartnerCenterCsv .\Packaging\Windows\StoreListing\ImportFolder\listingData.csv
```

The script:

- uses the newest Partner Center `listingData-*.csv` export, or the file passed with `-PartnerCenterCsv`;
- captures the five GUI scenes for every current `Assets/Localization` language and maps Partner Center locale columns to those assets;
- publishes the same ReadyToRun single-file shape used by release validation;
- captures all scenes declared in `store-screenshots.json` from a clean snapshot of `HEAD` and initializes an isolated Git repository in that temporary copy for the TUI scenes;
- isolates application settings and recent-workspace state in a temporary directory;
- opens a separate Windows Terminal window on the primary monitor for five TUI scenes in every current `Assets/Localization` language;
- verifies the exact TUI window handle before sending input and never targets another terminal window;
- demonstrates the workspace, `:format` schema hints, Action Palette, Markdown tree, and JSON tree;
- writes screenshots and `listingData.csv` under `ImportFolder`;
- maps localized TUI images to screenshot slots 6-10 for every Store locale using the same locale fallback rules as GUI media;
- produces `artifacts/store-screenshots/contact-sheet.png` and `tui-contact-sheet.png` for visual review;
- runs the existing Store listing validator.

Use `-PublishedExe <path> -SkipPublish` and `-CaptureHost <path> -SkipTuiBuild` to reuse existing binaries. Use `generate-store-screenshots.ps1` or `generate-store-tui-screenshots.ps1` when only one surface needs to be refreshed. The TUI script accepts `-Languages ru,en` for a targeted recapture; without it, every application language is captured. `-PlanOnly` validates locale mapping and scene declarations without publishing applications or opening windows.

The GUI capture protocol waits for observable UI state and rendered composition frames, then allows DWM a short final presentation guard before reading desktop pixels. TUI automation uses an English keyboard layout only for its own window, resets that window to the default font scale and full opacity with the built-in `Campbell` color scheme, expands it to the full primary-monitor width so the detailed parameter island remains visible, scales the logical `chromeLeft`, `chromeTop`, `chromeRight`, and `chromeBottom` crop values from 96 DPI to the actual terminal-window DPI, verifies a predominantly uniform client-frame perimeter, and fits the client area edge-to-edge into an opaque RGB `2048x1280` PNG without an added background. It exits the application normally and closes only the exact window it created. GUI content is not scaled.

Requirements: Windows 11 interactive desktop, Windows Terminal, primary working area of at least `2048x1280`, hidden desktop icons, and no overlapping always-on-top windows. Review both contact sheets before importing `listingData.csv` into Partner Center.
