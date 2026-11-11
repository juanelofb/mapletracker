# MapleTracker development and releases

User instructions are in README.md and the built-in Help window.

## OCR training experiment

See [docs/ocr-training.md](docs/ocr-training.md) for the local training and benchmarking workflow. The 1.1 build embeds both trained chat models from `data/tessdata` and `data/tessdata-fallback`. Experimental builds can set `MapleChatModelPath`, `MapleChatModelLicensePath`, and `MapleChatFallbackModelPath` to test other models. The optional font setting defaults off.

## Build

Use Windows x64 and the .NET SDK selected by global.json (10.0.403 or a compatible later patch in that feature band). NuGet access is needed for the first restore; the released app has no network dependency.

    ./scripts/Build.ps1
    ./scripts/Build.ps1 -Publish

For a portable SDK:

    ./scripts/Build.ps1 -Publish -DotNet C:/path/to/dotnet.exe

The script restores, builds Release, runs both check programs, and optionally publishes the Standalone profile. The release folder is `artifacts/MapleTracker-1.1`. The executable includes the README and third-party notices. Keep user settings and session logs out of release assets.

The Standalone profile uses self-contained Windows x64, a single executable, bundled native runtime libraries, compressed managed assemblies, and no trimming. WPF and the OCR reflection/dynamic interop are retained intact. Microsoft documents this deployment model at https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview.

## Assets and data

RuntimeAssets embeds the EXP table, English OCR model, native OCR libraries, required VC runtime components, guide and notices. It extracts OCR assets into the versioned local cache and checks their SHA-256 against the embedded bytes before use. VC dependencies are loaded before the OCR DLLs. Tesseract's CustomSearchPath points to that cache.

Tesseract 5.2.0 and its x64 Leptonica/Tesseract DLLs come from NuGet. data/native/x64 holds the three VC runtime redistributables from Visual Studio 2022 Community's VC/Redist/MSVC/14.42.34433/x64/Microsoft.VC143.CRT directory. This is redistributable runtime code, not Visual Studio tools. Preserve THIRD-PARTY-NOTICES.txt and the embedded licenses when repackaging. Microsoft distribution guidance: https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files.

Settings, journals and diagnostics use %LOCALAPPDATA%/MapleTracker. A legacy data/settings.json beside the executable can be imported once if new-format settings are absent. MAPLETRACKER_DATA_DIR overrides the data root for isolated QA. Tests set a process-local AppContext override, so they do not alter real user settings.

## Verification

The core checks cover parsers, custom terms, item matching, OCR variation suppression, numeric confirmation, level-up rollover, EXP losses, and cumulative ETA. WPF checks exercise modal setup/save/cancel, alert editing and delivery, focus preservation, statistics controls, appearance, and embedded-asset extraction/repair.

Optional local screenshot fixtures are selected using MAPLETRACKER_ORIGINAL_SCREENSHOT, MAPLETRACKER_LATEST_SCREENSHOT, MAPLETRACKER_FADING_SCREENSHOT, MAPLETRACKER_GAMEPLAY_FIXTURES, MAPLETRACKER_BADGE_SCREENSHOT and MAPLETRACKER_REPEAT_FIXTURES. User screenshots are not committed.

The standalone executable provides offline diagnostics:

    MapleTracker.exe --self-test C:/path/to/output
    MapleTracker.exe --verify-screenshot C:/path/to/game-client.png C:/path/to/output
    MapleTracker.exe --verify-screenshot C:/path/to/game-client.png C:/path/to/output C:/path/to/settings.json

Self-test initializes the bundled OCR model and writes runtime, model, table, guide, and native-module evidence to self-test.json. Screenshot verification writes crops, OCR candidates and validated HUD results. Both modes use an app-data directory under the requested output, not the user's normal settings.

For a release smoke check, copy only the EXE into an empty directory, point DOTNET_ROOT/DOTNET_ROOT_X64 to a nonexistent location, restrict PATH to Windows directories, and use a fresh DOTNET_BUNDLE_EXTRACT_BASE_DIR. Run self-test and a real screenshot check. Verify the native OCR/VC module paths point into the generated cache and the framework version matches the bundled runtime. This verifies dependency packaging on the test machine; it is not a substitute for testing supported clean Windows installations.

## Layout

- src/MapleTracker.Core: trade classification, watchlist matching, feed tracking, EXP table and session calculations.
- src/MapleTracker.App: WPF windows, visible Win32 capture, local OCR, bundled assets and data persistence.
- tests/MapleTracker.Checks: deterministic core checks.
- tests/MapleTracker.AppChecks: WPF, OCR and asset regression checks.

Capture uses the visible game client surface. The application has no game-memory reader, injection, network service, or gameplay/chat-input implementation.

## Offline world catalog

Run `python scripts/Get-WorldCatalog.py` to refresh the bundled OSMS public v1 API snapshot. This developer step needs network access. The script validates API schema, record counts, lengths and SHA-256 digests before replacing the bundled catalog. Keep `data/LICENSE-OSMS.txt` alongside it in source. Both files are embedded in the EXE.

`WorldCatalogStore` loads the newest valid local snapshot and checks the OSMS metadata once per launch. It fetches changed lists and validates the complete snapshot before replacing the cache. The tracker uses the new catalog during the current session. Map and item matching run locally. The startup refresh checks cover changed hashes, failed validation, and offline use. Set `MAPLETRACKER_OSMS_FIXTURES` to a folder containing a saved `api-meta.json` and the three original API payloads to test the public schema.

`GameCatalog` resolves canonical names with ambiguity and category guards. `LootCounter` retains raw pickup buckets and their original map IDs so later map evidence can reconcile totals without applying a new map to historical loot. Locally observed items are hints, not monster drop tables: OSMS supplies map monster placements but no server-side drops. Unknown matches remain visible rather than discarding their quantities.

`MapRecognition` locates the minimap's blue title panel independently of its body and caches unchanged pixels. It uses the stock OCR engine with title-specific preprocessing, leaving trained chat model selection unchanged. A manual Map title region disables automatic header location. Two agreeing map observations establish context; unreadable/transitioning maps immediately disable map-specific hints.

Core checks exercise canonical spelling, ambiguous prefixes, quantities, categories, map suffixes, evidence persistence and per-map reconciliation. WPF checks exercise the statistics title, removed details panel, corrected feed/raw text and map calibration. Optional real-map fixtures use `MAPLETRACKER_CONSTRUCTION_MAP`, `MAPLETRACKER_ANT_MAP`, and `MAPLETRACKER_ORIGINAL_SCREENSHOT`. `MAPLETRACKER_MAP_STATS_PREVIEW` saves the rendered statistics window for visual inspection.

## Meso notification replay

`MesoGlyphReader` uses bundled high-pass templates for the fixed `mesos (+` label, ten digits, and the closing parenthesis in the 1920×1080 English client. They were derived from manually verified notification crops; no screenshots are bundled. It checks the digit score and the gap to the next digit before reporting a reading. A visible row with uncertain digits does not become a competing OCR reading. The existing two-frame quantity confirmation and fade tracking still apply.

`MapleTracker.OcrBench record-loot <directory> <settings.json> [seconds]` exercises the app's visible-client capture on the foreground game and saves lossless frames locally. `replay-loot <directory> <report.json>` replays their timestamps through OCR and the pickup tracker. The regression set from Caution Falling Down includes stacked party EXP, simultaneous meso pickups, and empty frames. For its WPF integration checks, set `MAPLETRACKER_MESO_FIXTURES` to that local recording directory.
