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
2. Open **Setup**, choose the game window, and **Take snapshot**. Tracker windows briefly hide while the game is captured.
3. Choose each colored region and drag a tight box around **chat messages**, **loot notifications**, **level number**, and **EXP number plus percentage**. Include text, not just the colored EXP bar. Defaults match the original 1908×1080 reference image.
4. Use **Test recognition** to review the text. Fix the regions if needed, then **Save setup**.
5. Move both tracker windows away from the capture regions. Press **Start capture** and focus the game.

The app uses visible screen capture relative to the game client rectangle. Coordinates follow window movement and scale with resizing. Game UI layout changes still require recalibration. Capture and the active-session clock pause while the game is not the foreground window, is minimized, or another visible window overlaps a configured text region. This version does not capture a minimized or occluded game. Exclusive fullscreen is not supported.

**Demo** starts a clearly labeled simulated session, with no game required. It resets the current in-memory session. **New session** clears the current view and counters; previously written logs remain. **Pause** preserves the session. Closing the chat window exits both windows. Statistics can be closed and reopened. Opacity and always-on-top are configurable; positions and sizes are saved.

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

Ordered overlap across frames prevents unchanged lines from being added repeatedly and retains identical new lines when the feed visibly scrolls. An identical single-line message replaced by another identical message without a visible change is inherently ambiguous. Two consecutive empty observations clear the overlap history. Low-confidence lines are omitted, while HUD readings are checked against level requirements and the displayed percentage when available. Baselines, large jumps, losses and level changes require successive plausible observations. This is a first OCR implementation; verify it against your gameplay before relying on totals.

Recognition details are available in the statistics window. Setup's recognition test shows the raw OCR and validated HUD. For repeatable offline troubleshooting:

```powershell
./src/MapleTracker.App/bin/Release/net9.0-windows/MapleTracker.App.exe --verify-screenshot C:/path/to/screenshot.png C:/path/to/diagnostics
```

This writes original crops, processed crops, and `recognition.json`, using the default reference regions. It exits with code 1 on errors; see `data/startup-error.log` beside the executable.

## Data and development

Settings and session data live beside the executable in `data/`. Use a writable folder. Chat and item entries append to timestamped `data/sessions/*.jsonl`; the UI retains up to 5,000 entries. **Export** saves current entries, item totals and the statistics snapshot as JSON. Session history is not automatically restored after restart. Nothing is uploaded.

- `src/MapleTracker.Core` — trade matching, OCR text parsers, ordered line tracking, EXP table and session math.
- `src/MapleTracker.App` — WPF UI, Win32 screen capture, local Tesseract OCR, settings and logs.
- `tests/MapleTracker.Checks` — deterministic checks of rollover, losses, ETA, duplicate handling, parsers, categories and table totals.

```powershell
dotnet run --project tests/MapleTracker.Checks
dotnet run --project src/MapleTracker.App
```

Capture is currently GDI visible-screen capture. Windows Graphics Capture is a future upgrade for better occlusion handling. No game process memory access, injection, gameplay inputs or chat sending are implemented.

Third-party code: Tesseract .NET wrapper (Apache 2.0), Tesseract/Leptonica native dependencies, and the English tessdata_fast model (Apache 2.0; license included in `data/tessdata`).
