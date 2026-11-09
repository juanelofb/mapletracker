# MapleTracker

A local C# / WPF companion for MapleStory chat, loot and grinding sessions. Two independent, movable desktop windows keep the game clear. The first version targets Windows 10/11 x64 and .NET 9 Desktop Runtime.

## Run

Build with the .NET 9 SDK:

```powershell
./scripts/Build.ps1 -Publish
./artifacts/MapleTracker/MapleTracker.App.exe
```

The English Tesseract model is included. The script can download it again if missing. OCR runs locally; the running app has no network service or API key. Native Tesseract dependencies may require the Microsoft Visual C++ 2015–2022 x64 runtime on another PC.

1. Run the game in windowed or borderless mode.
2. Open **Setup**, choose the game window, and **Take snapshot**. Tracker windows briefly become transparent while the game is captured; the setup dialog stays open until you save or cancel.
3. Choose each colored region and drag a tight box around **chat messages**, **loot notifications**, **level number**, and **EXP number plus percentage**. Include text, not just the colored EXP bar. Defaults match the original 1908×1080 reference image.
4. Use **Test recognition** to review the text. Fix the regions if needed, then **Save setup**.
5. Move both tracker windows away from the capture regions. Press **Start capture** and focus the game.

The app uses visible screen capture relative to the game client rectangle. Coordinates follow window movement and scale with resizing. Game UI layout changes still require recalibration. Capture and the active-session clock pause while the game is not the foreground window, is minimized, or another visible window overlaps a configured text region. This version does not capture a minimized or occluded game. Exclusive fullscreen is not supported.

**Start capture / Pause** control OCR capture. The statistics window has independent **Start / Stop / Reset** controls: Stop freezes EXP, meso and the session clock while chat, loot and market alerts keep running; Start resumes from your current EXP and excludes EXP earned during the stopped interval. Reset clears only statistics and preserves their current running/stopped state. It leaves chat, item totals and alert history intact. Closing chat exits all tracker windows, including popups. Statistics can be closed and reopened.

The dark windows use joined tabs and a custom title bar. Drag the title bar to move them; drag the bottom-right grip to resize. The **Transparency** slider at the bottom of either window changes both window backgrounds while text remains fully opaque. Values run from 0% (opaque) to 60% transparent. Preferences, positions and sizes are saved. Demo has been removed.

## Item alerts

Open **Item alerts** in the chat window. Put one item per line in **I want to buy** or **I want to sell**, then **Save lists**. These lists are separate from the Buying/Selling chat tabs.

- Your buying list finds sellers: `Alice: S> Steel Ore` produces **Alice is selling Steel Ore**.
- Your selling list finds buyers: `Bob: B> Potions` produces **Bob wants to buy Potions**.
- Names match case-insensitively with punctuation and word boundaries. Use `|` for explicit aliases, for example `Gloves Attack Scroll | Glove att`. The first alias is the popup's display name. Matching does not guess misspelled OCR text; add aliases for abbreviations you see in chat.
- Item links before a marker and spaced markers such as `S >` are supported. Separate buying/selling clauses are matched by direction.
- Popups appear at the bottom-right of the monitor containing chat, inside its visible work area, without taking game focus. They are independent topmost windows, so minimizing chat does not hide them. A popup stays until dismissed; additional matches queue instead of replacing it. Dismiss the visible popup to see the next one.
- The **Popups** switch on chat, also available in Item alerts, disables visible popups and clears the popup queue. Match detection and **Alert history** continue while popups are off. History retains up to 200 entries and clears on app restart or a capture-source reset.
- The same recognized player, item and direction trigger at most once every 60 seconds. Other players still trigger independently.
- **Test popup** previews a notification using your draft list. The enable toggle and both lists are saved in settings.

## Tabs and calculations

- **All** contains every newly observed chat message; trade messages also appear in their matching tabs.
- **Buying / Selling** use literal, case-insensitive keywords with word boundaries. Edit comma-separated terms in Setup. A message can match both tabs. Player names before `:` are ignored for categorization.
- **Items** contains pickup notifications and a total per recognized item name. Truncated names stay truncated.
- EXP is read from the numeric HUD. The supplied `data/exp-table.json` provides the requirements for levels 1–99, using 64-bit integers. The sum to reach level 100 is 18,194,778,397. Level 100 has no fabricated next-level estimate.
- EXP gain is the difference in total character progression since the session baseline. Level-ups and EXP losses are accounted for. EXP notifications are not added again.
- Meso looted counts recognized pickup amounts; it does not measure wallet balance, spending or trading. Supported examples: `You have gained mesos (+125)` and `You received 1,250 mesos.` Actual server wording may need another parser rule.
- Per-minute/hour rates use active capture time. ETA uses the recent five-minute EXP pace and appears after 15 seconds of data. Stopping gains eventually clears the ETA. It is an estimate, not a guarantee.

## Recognition limits

The capture interval defaults to 500 ms. OCR runs on one background worker; frames never queue up. Small fonts, colored text, faded text, noisy backgrounds, wrapped chat and fast chat can reduce accuracy. A notification must be visible when a frame is captured. Text that the game hides or truncates cannot be recovered.

Chat uses ordered overlap across frames. Loot uses separate notification lifetimes, row positions, upward movement, and brightness changes, with two readable sightings required before counting a pickup or meso message. Missing OCR rows are retained briefly, so fading `Leaf` / `Leat` variants do not repeatedly add the same pickup. A fresh notification, or another identical row entering the feed, can still count separately. Low-confidence or very short-lived notifications may be missed. If a notification is replaced by an identical one without a visible change, the distinction is inherently ambiguous; this version favors avoiding inflated totals.

HUD recognition tries several contrast/segmentation settings and validates the numeric EXP value against its displayed percentage to within 0.03 percentage points. When one OCR pass reads the number well and another reads the percentage well, consistent evidence can be combined. Contradictory or partial evidence is rejected. Include both the number and percentage in your EXP selection. Baselines, large jumps, losses and level changes require successive plausible observations. The statistics window shows how long ago EXP was last confirmed; an old value is not proof that OCR is still succeeding. Verify OCR against gameplay before relying on totals or alerts.

Recognition details are available in the statistics window. Setup's recognition test shows the raw OCR and validated HUD.

During capture, `data/recognition-last.json` is refreshed every two seconds with the latest OCR candidates, loot row observations, and last confirmed EXP time. It stays local and contains no screen images.

For repeatable offline troubleshooting:

```powershell
./src/MapleTracker.App/bin/Release/net9.0-windows/MapleTracker.App.exe --verify-screenshot C:/path/to/screenshot.png C:/path/to/diagnostics
```

This writes original crops, processed crops, `recognition.json`, and `hud-attempts.json`, using the default reference regions. Optionally pass a settings JSON path as the last argument to use your saved regions. It exits with code 1 on errors; see `data/startup-error.log` beside the executable.

## Data and development

Settings and session data live beside the executable in `data/`. Use a writable folder. Chat and item entries append to timestamped `data/sessions/*.jsonl`; the UI retains up to 5,000 entries. **Export** saves current entries, item totals and the statistics snapshot as JSON. Session history is not automatically restored after restart. Nothing is uploaded.

- `src/MapleTracker.Core` — trade matching, OCR text parsers, ordered line tracking, EXP table and session math.
- `src/MapleTracker.App` — WPF UI, Win32 screen capture, local Tesseract OCR, settings and logs.
- `tests/MapleTracker.Checks` — deterministic checks of rollover, losses, ETA, duplicate handling, parsers, categories and table totals.
- `tests/MapleTracker.AppChecks` — WPF regression checks of setup, saved watchlists, background popup delivery with chat minimized, popup queueing and dismissal, monitor bounds, focus preservation, independent statistics controls, and transparency. These use a synthetic capture source and briefly open test windows. Optional `MAPLETRACKER_ORIGINAL_SCREENSHOT`, `MAPLETRACKER_LATEST_SCREENSHOT`, and `MAPLETRACKER_FADING_SCREENSHOT` environment variables run OCR checks against local game-client image fixtures; user screenshots are never committed.

```powershell
dotnet run --project tests/MapleTracker.Checks
dotnet run --project tests/MapleTracker.AppChecks
dotnet run --project src/MapleTracker.App
```

Capture is currently GDI visible-screen capture. Windows Graphics Capture is a future upgrade for better occlusion handling. No game process memory access, injection, gameplay inputs or chat sending are implemented.

Third-party code: Tesseract .NET wrapper (Apache 2.0), Tesseract/Leptonica native dependencies, and the English tessdata_fast model (Apache 2.0; license included in `data/tessdata`).
