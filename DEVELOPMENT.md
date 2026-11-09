# MapleTracker development and releases

User instructions are in README.md and the built-in Help window.

## Build

Use Windows x64 and the .NET SDK selected by global.json (10.0.403 or a compatible later patch in that feature band). NuGet access is needed for the first restore; the released app has no network dependency.

    ./scripts/Build.ps1
    ./scripts/Build.ps1 -Publish

For a portable SDK:

    ./scripts/Build.ps1 -Publish -DotNet C:/path/to/dotnet.exe

The script restores, builds Release, runs both check programs, and optionally publishes the Standalone profile. The release folder is artifacts/MapleTracker-1.0. Only MapleTracker.exe is required; the README and third-party notices are optional external copies of embedded documents. No user settings or session logs belong in a distributable release.

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
