# Microsoft Store media

Store media is generated in an interactive Windows session. GUI screenshots use the published `win-x64` single-file application and include the native title bar, window shadow, acrylic background, and a controlled amount of desktop around the window. TUI screenshots use a dedicated capture host inside an isolated Windows Terminal window and crop away all terminal chrome.

Run from the repository root:

```powershell
.\Scripts\generate-store-media.ps1 `
  -PartnerCenterCsv .\Packaging\Windows\StoreListing\ImportFolder\listingData.csv
```

The script:

- uses the newest Partner Center `listingData-*.csv` export, or the file passed with `-PartnerCenterCsv`;
- captures the five GUI scenes for every current `Assets/Localization` language and maps Partner Center locale columns to those assets: the project tree with preview, a Live context session, the MCP menu, the agent journal, and the settings panel;
- publishes the same ReadyToRun single-file shape used by release validation;
- captures all scenes declared in `store-screenshots.json` from a clean snapshot of `HEAD` and initializes an isolated Git repository in that temporary copy for the TUI scenes;
- isolates application settings and recent-workspace state in a temporary directory;
- runs real MCP sessions for the Live context, MCP menu, and agent journal scenes (see below);
- opens a separate Windows Terminal window on the primary monitor for five TUI scenes in every current `Assets/Localization` language;
- verifies the exact TUI window handle before sending input and never targets another terminal window;
- demonstrates the workspace, `:format` schema hints, Action Palette, Markdown tree, and JSON tree;
- writes screenshots and `listingData.csv` under `ImportFolder`; an existing `listingData.csv` is edited in place, so only the GUI screenshot path rows change and its encoding, header, quoting, text rows, and TUI rows stay byte-for-byte identical;
- maps localized TUI images to screenshot slots 6-10 for every Store locale using the same locale fallback rules as GUI media;
- produces `artifacts/store-screenshots/contact-sheet.png` and `tui-contact-sheet.png` for visual review;
- runs the existing Store listing validator when the output is the repository `ImportFolder`.

Use `-PublishedExe <path> -SkipPublish` and `-CaptureHost <path> -SkipTuiBuild` to reuse existing binaries. Use `generate-store-screenshots.ps1` or `generate-store-tui-screenshots.ps1` when only one surface needs to be refreshed. Both the GUI and the TUI script accept `-Languages ru,en` for a targeted recapture; without it, every application language is captured. `-PlanOnly` validates locale mapping and scene declarations without publishing applications or opening windows.

The GUI script also accepts `-Scenes` to refresh only some slots. A scene is named by its directory, index, or name (`3_Mcp_Menu`, `3`, or `Mcp_Menu`), several are separated by commas, and the selector combines with `-Languages`:

```powershell
.\Scripts\generate-store-screenshots.ps1 -SkipPublish `
  -PublishedExe .\artifacts\store-screenshots\publish\DevProjex.exe `
  -Scenes 3_Mcp_Menu
```

The application still walks through every scene, because later scenes build on the state of earlier ones, but only the selected scene folders are written; every other GUI slot and all TUI slots stay untouched. The GUI screenshot rows of `listingData.csv` are rewritten to the same deterministic paths as in a full run, so captions and other rows do not change, and the validator runs as usual. The contact sheet shows only the selected scenes. With `-PlanOnly`, the selected scenes are listed after `GUI scenes:` and an undeclared scene is rejected.

The GUI capture protocol waits for observable UI state and rendered composition frames, then allows DWM a short final presentation guard before reading desktop pixels. TUI automation uses an English keyboard layout only for its own window, resets that window to the default font scale and full opacity with the built-in `Campbell` color scheme, expands it to the full primary-monitor width so the detailed parameter island remains visible, scales the logical `chromeLeft`, `chromeTop`, `chromeRight`, and `chromeBottom` crop values from 96 DPI to the actual terminal-window DPI, verifies a predominantly uniform client-frame perimeter, and fits the client area edge-to-edge into an opaque RGB `2048x1280` PNG without an added background. Repository copies are subsequently palette-quantized with libimagequant to reduce Store listing weight. The stored-file contract is a fully opaque `2048x1280` PNG in either truecolor or indexed format. It exits the application normally and closes only the exact window it created. GUI content is not scaled.

## Agent session scenes

GUI scenes 2-4 show a real Live context session rather than staged text. The `agentSessions` section of `store-screenshots.json` declares the window selection and the scripted MCP sessions, and the GUI script drives them for every language:

- before the window starts, each `earlier` session runs `DevProjex.exe mcp --root <capture project>`, with `--live` when declared, initializes with the declared `clientInfo` name and version, makes its tool calls, and closes stdin so the journal records a completed session;
- the window opens the project, enables **View → Agent activity**, ticks every `liveSelection` path, and saves that selection;
- the script then starts the `live` session (`mcp --live`, client `claude-code`, which the window presents as Claude Code), waits until the window reports that it observed the session, and only then sends the tool calls, because the window marks only calls made after it first saw the session;
- the window waits until its delivery counts reflect every call, expands the folders of the delivered files so their `✦` markers are visible, and captures the Live context scene; it then opens **MCP → Live context** for the menu scene and opens **MCP → Journal…** through the regular dialog surface for the journal scene;
- the live server keeps running until the window has closed, then ends through stdin like a real client.

Every GUI scene uses the same window size. Agent activity appears only as `✦` delivery markers in the tree; the status bar carries no agent text. The Live context scene therefore shows the tree beside the settings panel with the preview closed, and opens the product's own delivery-marker tooltip, anchored beside the marker, on the file the agent received most often. The scripted calls deliver one file five times so that count reads naturally in every language. The MCP menu scene keeps that layout, the tree beside the settings panel with the preview closed, so the open menu stays the focal point, and highlights **Open in Claude Code** as keyboard navigation would; nothing is invoked. The journal scene returns to the tree workspace, whose delivery markers match the calls the journal itemizes. Capture mode removes the version from the main window title so the images do not date themselves.

Every server process and the window use the language's capture `app-data` directory through `DEVPROJEX_INTERNAL_DATA_ROOT`, so the journal, live-session heartbeats, saved selections, and desktop-control registration never reach the user's data directories. The scripts never run `mcp connect`, never invoke the **Open in …** items, and never write client configuration. Program caches shared by every DevProjex run, such as extracted parser grammars, are not redirected. The capture window formats numbers and dates in the culture of the captured language, and the controller re-activates the capture window before each frame so the title bar is captured in its active state, except for the journal scene, where the journal window keeps the foreground.

Requirements: Windows 11 interactive desktop, Windows Terminal, primary working area of at least `2048x1280`, hidden desktop icons, and no overlapping always-on-top windows. Review both contact sheets before importing `listingData.csv` into Partner Center.

CSV files generated from an export use the English `Type` header, and an import CSV edited in place keeps the header it has; capture and validation also accept the localized export form `Type (...)`, so a Partner Center export made in any portal language stays compatible without edits.
