# dsDock Desktop Memo

**English** | [简体中文](README-zh.md)

> A card-based memo / widget app that \*\*lives on the desktop, stays always on top, and can be collapsed into a sidebar.\*\*
> Windows 10 / 11 x64 · .NET 8 + WPF · \*\*zero third-party dependencies\*\* (builds offline)
>This project is assisted by DeepSeek V4.1 Flash.

## 1\. Introduction

\-The modularized floating window tool currently features time display and sticky note functionality, with more modules under development.



## 2\. Getting started

### 2.1 Users: install and run

1. Double-click `dsDock-Setup.exe` (single-file self-extracting package) — the install wizard appears directly
2. Choose the install location (**defaults to `D:\\dsDock`**; installing to C: is not recommended — see §7, limitation 1)
3. Tick the options: desktop shortcut / start with Windows / launch immediately after install
4. When finished, double-click the tray icon or the desktop shortcut

Requirements: Windows 10/11 x64 + **.NET 8 Desktop Runtime** (`Microsoft.WindowsDesktop.App`).

### 2.2 Developers: build / run / verify

```powershell
# Build (app + all registered cards, staged into <repo root>\\Cards\\<id>\\)
powershell -ExecutionPolicy Bypass -File build.ps1

# Build + deploy outside the workspace + launch (tray icon works; recommended)
tools\\deploy.cmd

# Full automated self-test (60+ checks, data-isolated, never touches your real config)
tools\\validate.cmd

# Rebuild the installer (app -> payload -> setup -> single-file SFX, output in dist\\)
powershell -ExecutionPolicy Bypass -File tools\\make-installer.ps1
```

> ℹ️ This repository deliberately \*\*ships no .sln\*\*: cards are plugins and must be built and staged into `Cards\\<id>\\` by the scripts (a solution file cannot do that step), so use the scripts above, or build `src\\DsDock.App\\DsDock.App.csproj` directly.

\---

## 3\. Layout

### 3.1 Repository

```
dsDock0.1\\
├─ build.ps1 / build.cmd        build + stage cards
├─ run.cmd                      build + deploy outside the workspace + launch
├─ registry.json                list of registered card ids
├─ Cards\\                       staged card output (gitignored; one folder per manifest.id)
├─ docs\\install-readme.txt      install notes (copied into dist\\ when packaging)
├─ dist\\                        deliverables (installer)
├─ tools\\
│   ├─ deploy.ps1 / .cmd        deploy outside the workspace (default D:\\dsDock)
│   ├─ validate.ps1 / .cmd      self-test entry point
│   └─ make-installer.ps1       packaging: app -> payload -> setup -> single-file exe
└─ src\\
    ├─ DsDock.Card.Abstractions\\  card contract (shared by the host and every card)
    ├─ DsDock.App\\                host: windows / tray / card runtime / storage / settings UI / self-test
    │   ├─ Shell\\                 single instance, message window, tray icon, fullscreen watcher, autostart
    │   ├─ Windows\\               main panel, sidebar, popups, card library, settings window, window animator
    │   ├─ Plugins\\               registry reading, isolated loading (shadow copy), card runtime, install/remove
    │   ├─ Storage\\               settings / layout / backups / import \& export
    │   ├─ Controls\\              self-drawn controls (slider, switch, hover highlight, menus)
    │   ├─ Anim\\                  60 FPS frame clock
    │   ├─ Platform\\              window helpers, modern folder picker
    │   └─ Diagnostics\\           command-line options, self-test M1/M2, logging
    ├─ Cards\\Clock\\               clock card (source)
    ├─ Cards\\StickyNote\\          sticky note card (source, includes reminders)
    └─ DsDock.Setup\\              installer (WinForms wizard + embedded payload)
```

### 3.2 Runtime layout after installation

```
<install dir>\\
├─ DsDock.exe / DsDock.dll / DsDock.runtimeconfig.json
├─ tray.ico / Assets\\
├─ registry.json                {"cards":\["clock","sticky\_note", ...]}
├─ Cards\\<id>\\                  manifest.json + <entry>.dll + icon.png
├─ data\\                        <- user data (never overwritten by upgrades/reinstalls)
│   ├─ settings.json            appearance and behaviour settings
│   ├─ layout.json              container layout (row count + each card's position and size)
│   ├─ cards\\<instanceId>.json  per-card state
│   ├─ backup\_1..3.json         rotating backups
│   └─ export\\                  default export folder
└─ Uninstall.cmd                uninstall script
```

> If the program folder is not writable, data automatically falls back to `%APPDATA%\\桌面备忘录\\` (Desktop Memo).

\---

## 4\. Feature overview

### 4.1 Windows and interaction

|Feature|Details|
|-|-|
|Always on top|Both the panel and the sidebar are TOPMOST, **immune to Win+D**; no taskbar button (TOOLWINDOW)|
|Collapse / expand|200 ms easing, "grows out of" / retracts into the sidebar's edge; the animation only changes the window rectangle, so **cards are never re-laid out**|
|Clicking the sidebar|Toggles **expand ⇄ collapse** (shares a single entry point with the tray menu)|
|Dragging the sidebar|**1:1 tracking** (based on the window position captured on mouse-down plus the total cursor delta — never accumulated); on release it **snaps to the nearest screen edge**|
|Sidebar length|Follows the panel's length, with a **lower bound of 10 % of the screen dimension**|
|Hover highlight|The sidebar brightens (opacity +0.6 and the border turns into the accent colour, 150 ms); button hover is unified through `DsHover`|
|Theme|Opacity (applied live) / corner radius / font size / accent colour / row count; all persisted to `settings.json`|
|Frame rate|One shared 60 FPS frame clock (`Anim\\FrameClock`)|
|Pop-up windows|Settings / card library / card editor / prompts open **near the mouse cursor**, flip upwards near the screen edge and are clamped into the work area|
|Fullscreen yielding|When a fullscreen app or game takes the foreground, the panel **slides out to the nearest left/right edge** (200 ms) and hides; leaving fullscreen **slides it back to its previous position**|
|Display changes|`WM\_DISPLAYCHANGE` / `WM\_DPICHANGED` → re-fit into the work area and re-lay out|

### 4.2 Cards

|Card|Sizes|Details|
|-|-|-|
|**Clock**|1×1 / 2×1|12/24-hour format **follows the system locale**, date too; seconds can be toggled (context menu, state persisted)|
|**Sticky note**|1×1 / 2×1 / 2×2|Same card style as the clock (no filled background, border follows the chosen colour); completion dot in the top-left; 「编辑」(Edit) button bottom-left; 6 highly saturated preset colours; the editor saves with **Enter** and inserts a newline with Shift+Enter; 100-character limit; **reminders** (fire a Windows system notification when due)|

* **Card library** (▦): 4 columns × 3 rows per page, showing name / description / instance count in the container; the Add button stays available until the limit, then reads「已达上限 N」; each tile also has a remove button
* **Import new card…**: pick a folder → validate the manifest → copy into `Cards\\<id>\\` → write `registry.json` → **hot-load, no restart** (tolerates an extra top-level folder after unzipping; automatically skips forbidden files such as `DsDock.Card.Abstractions.dll`)
* **Remove card…**: opens a list of installed cards; the Remove button on a row removes all instances of that card from the container, deletes its folder, unregisters it from `registry.json` and unloads it from the runtime — **synchronised live, no restart**
* **Failure isolation**: a broken card is simply skipped (the library shows a grey placeholder with the reason and a system notification pops once); every other card keeps working

### 4.3 Tray

* **Left click**: expand ⇄ collapse the main panel (both `NIN\_SELECT` and `WM\_LBUTTONUP` protocols are recognised)
* **Right click**: self-drawn menu — show/hide the panel, Settings, Card library, Start with Windows (toggle), Exit
* Automatically re-registers after Explorer restarts (`TaskbarCreated`)
* Card load failures / sticky-note reminders are delivered as **system notifications** through the tray balloon

### 4.4 Data

* Atomic writes (temp file + `File.Replace`, falling back to overwrite)
* **Rotating backups** `backup\_1..3.json` (whole-package snapshot: settings + layout + card states)
* Corruption tolerance: parse failures are reported and a fallback to the backups is attempted — the app never crashes
* **Export / import**: user-chosen paths, format validation and filtering (`\*.dsdock.json`); bad files are rejected with a reason and a backup is taken before importing; **the start-with-Windows state travels with the package too**
* **Reset all data**: confirmation → backup → wipe → automatic restart (the new process passes `--restart-wait` so it waits for the old instance to release the single-instance mutex)

\---

## 5\. Command-line options

|Option|Description|
|-|-|
|`--selftest <json path>`|Run the full self-test and write the report to the given file (during the self-test, settings/layout/card states use `.selftest`-suffixed isolated files, so **real data is never touched**)|
|`--hold-ms N`|Keep the process alive for N ms after the self-test (for external sampling)|
|`--force-data-root <dir>`|Force the data directory (used by the self-test)|
|`--restart-wait`|Used when restarting itself: wait for the old instance to exit and release the single-instance mutex|
|`--edge Left\|Right\|Top\|Bottom`|Sidebar dock edge|
|`--sidebar-width N` / `--sidebar-alpha N`|Sidebar thickness / opacity|
|`--alpha N` / `--corner N` / `--font N` / `--accent #RRGGBB` / `--rows N`|Appearance overrides|
|`--locked` / `--no-auto-expand`|Start locked / do not expand the panel on startup|
|`--no-tray`|Do not install the tray icon (debugging)|
|`--x N --y N`|Force the main panel position|

\---

## 6\. Testing

* Self-test entry point: `tools\\validate.cmd` (or `DsDock.exe --selftest <path>`); **60+ assertions** covering: window styles / animation geometry / the card system / isolated loading / failure isolation / clock and sticky-note behaviour / reminders / data persistence and backups / export-import and format validation / card import and removal / tray event parsing / fullscreen yielding and snapping / DPI maths / hover and controls
* Reports: the self-test JSON (structure plus per-case detail) and the log `data\\logs\\app-\*.log`
* **The whole self-test is data-isolated** (`.selftest`-suffixed files) and never touches your real configuration
* Manual confirmation checklist (interactions the self-test cannot cover): tray left/right click, fullscreen yielding and return, changing the system scale on a real machine, restarting Explorer, double-clicking the single-file installer

\---

## 7\. Known limitations and caveats

1. **The tray icon needs a non-restricted environment**: a process started from a restricted directory such as `D:\\dshwk001\\...` can be dropped to low integrity, and `Shell\_NotifyIcon` is then rejected by the system through UIPI (`err=5`) → **install/deploy into an ordinary directory such as `D:\\dsDock`** (which is what the installer defaults to).
2. **On Windows 11 the tray icon hides in the `^` overflow area by default**: drag it from the overflow area onto the taskbar to keep it visible (this preference is recorded by Windows per exe path; the program cannot do it for you).
3. The **.NET 8 Desktop Runtime** is required (the package is framework-dependent and does not include the runtime).
4. The single-file installer is produced by the built-in Windows `iexpress`: **its wrapper icon is the system default**, it is **unsigned** (SmartScreen may prompt once) and it **does not accept arguments** (for silent installs use `dist\\DsDockSetup\\DsDockSetup.exe --silent --dir ...`; that folder is produced by the packaging script).
5. **Overwriting an existing card with an updated version** removes that card's instances from the container (the instances belong to the old assembly and cannot be swapped safely), so they must be added again from the card library; the data files are cleaned up as well, so you get clean new instances.
6. Fullscreen yielding relies on foreground-window detection: a few launchers or borderless fullscreen applications may not be recognised (report the specific program and the detection can be adjusted).
7. One **timing-sensitive self-test case** (sampling card sizes during an animation) occasionally fails to capture a sample and shows red; that is a sampling-timing artefact rather than a functional regression, and retry-based tolerance is being added.

\---

## 8\. Troubleshooting

|Symptom|Likely cause|What to do|
|-|-|-|
|No tray icon|Started from a restricted directory (low integrity rejected) / Windows 11 overflow area|Install into a directory such as `D:\\dsDock`; drag the icon out of the `^` overflow area|
|Tray left/right click does nothing|Event-protocol parsing (in v4 the event is in the low word of `lParam`)|Check the `托盘回调: …` lines in the log; this issue is fixed|
|Importing a card reports `being used by another process`|Older builds mapped the card DLL directly|Loading now uses a shadow copy — update to the latest build|
|The card library shows a grey placeholder plus a reason|Invalid manifest / corrupted DLL / `factory.Id` does not match the registered id|Fix the card package as suggested (see the card development spec, §4 and Appendix A)|
|The window closes itself or crashes after clicking Import/Remove|A dialog taking focus caused the self-closing behaviour|A `\_dialogOpen` guard was added; if it still happens, send us the last 20 lines of the log|
|Data looks wrong|`layout.json` or a card state file is corrupted|The app reports it and tries to fall back to `backup\_1..3.json`; you can also restore manually with "Import data" in Settings|
|Cannot bring the panel back after collapsing / only half of it appears|An animation was cancelled by another animation (a historical issue)|Fixed with per-owner sequence numbers; if it reproduces, attach the log|



