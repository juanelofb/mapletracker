# MapleStory OCR training experiment

This document covers the development workflow for MapleTracker's optional trained chat reader. MapleTracker 1.1 ships both trained models for the compact white chat bar. Loot and HUD recognition have their own checks.

## What is ready

- `scripts/ocr/dataset.py`: import and review single-line PNG/transcript pairs, validate them, export a reproducible training/test split, and score prediction reports. It needs only Python's standard library.
- `tools/MapleTracker.OcrBench`: read the same prepared images with either the bundled model or a candidate `.traineddata` file. It uses MapleTracker's bundled native OCR libraries and isolates its cache from the player's settings.
- Local gameplay samples stay outside Git. `experiments/ocr/` is ignored; private captures can also stay in a workspace `work` directory.

The original six-line starter dataset only demonstrates the workflow. Larger experiments should keep their own source manifest, reviewed labels, fixed splits, model hashes, and comparison reports.

## Batch screenshot helper

To collect new examples automatically, double-click `scripts/Start-MapleMessageCapture.cmd` on the Windows PC running the game. Its standalone PowerShell script requires no OCR model, Python, SDK, or running tracker. It reads the preview profile's saved compact-chat region (or the normal profile, then the default 1920×1080 calibration), watches text-shape pixels every 100 ms, waits for 300 ms of stability, and saves a lossless full game-client PNG on a new visible state. It takes one initial stable sample, ignores blank states, and suppresses unchanged/recently captured text shapes without transcribing anything.

Images go to the Windows Desktop's `screenshotsmaple` folder, including redirected/OneDrive Desktops. The script stops at **1,000 total existing plus new image files**; it does not overwrite earlier images. A folder lock prevents two collectors using the same output simultaneously. `captures.jsonl` records the actual capture region, image dimensions, and image/text-shape hashes, and allows exact duplicate suppression across restarts. This journal contains no invented transcripts.

Focus the game and keep the whole game client visible on-screen. Alt-tabbing, minimizing, and overlapping windows pause collection. Ctrl+C in the collector console stops early. Keep the compact white chat bar visible and avoid typing during collection: pixel detection cannot distinguish drafts from received messages, and a message that disappears before the settle interval may be missed. An identical repeated message with no visible change cannot be detected as a new event. Leave the script and its launcher together.

Optional commands:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Capture-MapleMessages.ps1 -SelfTest
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Capture-MapleMessages.ps1 -OutputDirectory C:/captures/messages -Limit 1000
```

The self-test uses synthetic pixels and never captures the screen or writes to Desktop. The optional output path is useful for isolated QA. `-SettingsPath`, `-WindowTitle`, `-PollMilliseconds`, and `-SettleMilliseconds` can override the defaults. Changes to the tracker calibration require restarting the collector.

The optional benchmark can extract the compact chat bar from the November 10 capture layout:

```powershell
dotnet tools/MapleTracker.OcrBench/bin/Release/net10.0-windows/MapleTracker.OcrBench.dll extract C:/captures/screenshots experiments/ocr/corpus-01
dotnet tools/MapleTracker.OcrBench/bin/Release/net10.0-windows/MapleTracker.OcrBench.dll normalize experiments/ocr/corpus-01/raw experiments/ocr/corpus-01/normalized
```

This helper is specific to 1920×1080 JPG/PNG captures and the chat box at x=403, y=989, width=739, height=37. It creates raw/prepared PNGs, unreviewed OCR suggestions, source hashes, and numbered contact sheets. The separate normalization pass removes the bar borders and converts text/background contrast into black text on white. Both commands require a new output directory and leave source images unchanged. Do not use this fixed capture profile for another UI position or scale without adjusting and verifying its bounds.

Review the contact sheets and original crops before accepting transcripts. Frame count is not a count of distinct training examples. Keep repeated messages and copied ad variants together when choosing train, validation, and test folds.

For a numeric-focused correction trial, the optional `numeric-samples` command proposes close-up crops from training lines only:

```powershell
dotnet tools/MapleTracker.OcrBench/bin/Release/net10.0-windows/MapleTracker.OcrBench.dll numeric-samples C:/dataset/train experiments/ocr/numeric-candidates
```

Review every proposed crop against its target before mixing approved pairs with the original full-line training examples. A word bounding box can include neighboring punctuation even when its recognized text matches a numeric token. Keep derivative crops in the source line's training fold and report them as additional training views, not independent messages. `scripts/ocr/critical_metrics.py` provides separate checks for names, digit sequences, explicit trade tags, and k/m price tokens. Freeze regression limits before training; fewer overall character edits alone does not justify a replacement model.

## 1. Collect and review text lines

Keep the compact white chat bar visible. Use several gameplay sessions and include ordinary chat, player names, item links, buying/selling tags, percentages, prices, and punctuation. A practical initial target is 200–500 distinct, readable lines, followed by more samples if evaluation shows gaps; this is a pilot target, not an accuracy guarantee. Include normal font sizes and game-window scales you intend to support.

Capture the game client, rather than the entire desktop. Existing screenshot diagnostics produce the prepared pixels used by the actual OCR pipeline:

```powershell
./MapleTracker.exe --verify-screenshot C:/captures/game-client.png C:/captures/sample-001 C:/captures/settings.json
```

Use a copy of the player's saved settings matching that client image and layout. Diagnostics use an isolated data directory. For white-bar mode, `chat-white-bar-pass0-ocr.png` is one model-ready text line. Review it beside its `-original.png` counterpart. Keep source captures locally so a questionable label can be checked later. Setup's recognition-test popup alone does not export these diagnostics.

Use only one readable text line per sample. Do not include the channel/ear badge in a transcript: use a prepared sender/body crop where the badge is omitted. Exclude clipped or unreadable text rather than inventing missing letters. Transcribe exactly what is visible, including capitalization, item brackets, `B>`, `S>`, digits, and punctuation. An OCR suggestion becomes ground truth only after visual review.

From the repository root:

```powershell
python scripts/ocr/dataset.py add --root experiments/ocr/dataset --image C:/captures/sample-001/chat-white-bar-pass0-ocr.png --group session-001 --kind chat --text 'Alice : S> GFA 60% 1.4m' --reviewed
```

The example text is illustrative: replace it with the exact visible text. Use the same group for all captures from the same session and all alternate preparations of the same message. Each entry becomes `sample-ID.png` plus `sample-ID.gt.txt`, with a local manifest. Without `--reviewed`, it is an unreviewed suggestion excluded from export. Correct and approve an entry with:

```powershell
python scripts/ocr/dataset.py review --root experiments/ocr/dataset --id sample-ID --text 'the corrected visible text'
python scripts/ocr/dataset.py validate --root experiments/ocr/dataset
```

Keep chat in its own dataset for the initial experiment. Use `--kind loot`, `exp`, or `level` in separate datasets when evaluating those fonts later. The splitter does not filter kinds.

## 2. Freeze an independent test set

```powershell
python scripts/ocr/dataset.py split --root experiments/ocr/dataset --output experiments/ocr/split-01 --test-fraction 0.2 --seed 42
```

This exports `train/`, `test/`, and `split.json`. Only reviewed samples are included. Same-session samples, identical images, and identical normalized transcripts stay together, including transitive links between groups. Consequently the ratio may differ from 80/20. At least two independent groups are required. The exporter refuses to overwrite an existing snapshot.

Use `train/` for fitting and internal validation. Keep `test/` out of training and checkpoint selection. Collect another session for a final test if repeated comparisons begin influencing model choices. Never count duplicate frames as independent examples.

## 3. Measure the current model

Build the optional benchmark with the .NET SDK selected by `global.json`:

```powershell
dotnet build tools/MapleTracker.OcrBench/MapleTracker.OcrBench.csproj -c Release
dotnet tools/MapleTracker.OcrBench/bin/Release/net10.0-windows/MapleTracker.OcrBench.dll experiments/ocr/split-01/test experiments/ocr/baseline.json bundled eng singleline
python scripts/ocr/dataset.py score --dataset experiments/ocr/split-01/test --predictions experiments/ocr/baseline.json --output experiments/ocr/baseline-score.json
```

The benchmark reads already-prepared pixels without resizing, thresholding, or guessing corrections. It measures the model's single-line reading rather than the complete app's multiple-pass candidate selection or duplicate tracking. Engine startup and one warmup are excluded from per-line times. Reports record image/model SHA-256, segmentation mode, and runtime. Use the same preparation and segmentation mode for baseline and candidate.

Scores include case-sensitive character error rate, exact-line accuracy, mean and p95 OCR milliseconds, and expected/actual text for every sample. Unicode NFC and whitespace are normalized; punctuation, case, and prices are preserved. Character error rate can exceed 100% when a model inserts many characters. Compare individual mistakes, especially numbers and trade direction, alongside the aggregate score. The scorer rejects missing, repeated, extra, or mismatched-image predictions.

## 4. Fine-tune a trainable English model

MapleTracker currently ships `tessdata_fast/eng.traineddata`. Its integer model cannot be the fine-tuning starting point. Obtain the floating-point English model from the official [tessdata_best repository](https://github.com/tesseract-ocr/tessdata_best), retain its license, and record its SHA-256 and source revision. Benchmark this unmodified model too: a gain over the fast model alone does not demonstrate a gain from MapleStory training.

The official [tesstrain workflow](https://github.com/tesseract-ocr/tesstrain) supports Linux and a native Windows setup with its documented prerequisites. The app's OCR DLL does not include the training executables. For the initial unchanged-character-set experiment, `scripts/ocr/train_native.py` also runs the native executables directly, using Python's standard library instead of GNU make and Pillow. It writes line boxes in the format used by tesstrain and preserves the base model's character set and dictionaries.

Portable training executables can be extracted into a workspace from the Windows installer linked by the Tesseract project. Keep their licenses and record the installer/model hashes. This does not require replacing the app's OCR DLLs or installing a system-wide runtime. The initial experiment used the portable 5.5.3 tools; the candidate was then tested with the app's actual bundled OCR library.

For the native runner, prepare separate train and validation directories first, leaving the external test set untouched. Input images should have dark text on a light background. Check the pixels stored in the generated `.lstmf` files: runtime automatic inversion should not be assumed to happen during training. Preserve any required image preparation when benchmarking and deploying the candidate.

```powershell
python scripts/ocr/train_native.py --tools C:/tools/tesseract --base-model C:/models/eng.traineddata --train C:/dataset/train --validation C:/dataset/validation --output experiments/ocr/run-01 --iterations 400
```

The runner generates isolated training files, uses LF line endings for native Windows training lists, limits OpenMP to two threads, and exports floating-point and integer candidates. It refuses to reuse an output directory or accept overlapping train/validation sample IDs. Its first version accepts ASCII transcripts only; extending the alphabet requires reviewing the character-set workflow. Training files and checkpoints stay outside Git.

Alternatively, after installing tesstrain's prerequisites, clone that workflow and install its Python requirements. In that checkout, a first bounded experiment is:

```bash
make training MODEL_NAME=maple_chat START_MODEL=eng \
  TESSDATA=/absolute/path/to/tessdata_best \
  GROUND_TRUTH_DIR=/absolute/path/to/split-01/train \
  MAX_ITERATIONS=1000 RATIO_TRAIN=0.9 PSM=7
```

Use real absolute paths without spaces. Keep `test/` separate; tesstrain makes its own internal training/validation split within `train/`. The iteration limit is an initial experiment setting, not a required amount of training. Save the tesstrain revision, command, seed, logs, and input manifest. The main output is `data/maple_chat.traineddata`; `make traineddata MODEL_NAME=maple_chat` additionally exports floating/integer checkpoint variants. Do not select checkpoints using the external test set.

Tesseract's [training documentation](https://tesseract-ocr.github.io/tessdoc/tess5/TrainingTesseract-5.html) explains the model and fine-tuning constraints.

## 5. Compare before changing the app

Put the candidate in an isolated model directory as `maple_chat.traineddata`:

```powershell
dotnet tools/MapleTracker.OcrBench/bin/Release/net10.0-windows/MapleTracker.OcrBench.dll experiments/ocr/split-01/test experiments/ocr/candidate.json C:/models maple_chat singleline
python scripts/ocr/dataset.py score --dataset experiments/ocr/split-01/test --predictions experiments/ocr/candidate.json --output experiments/ocr/candidate-score.json
```

Run the same benchmark on the unmodified floating-point English model and any proposed integer candidate. Compare accuracy and time on the same PC. Then replay held-out gameplay through the actual app pipeline, including message parsing, alerts, item/meso counts, and EXP validation. Better character recognition alone does not prove better feed tracking. Preserve the previous model until the replacement has demonstrated an improvement across normal layouts and scales.

## Optional guarded chat preview

The original single-model preview runs the regular compact-chat reader first, then tries the trained font model. It selects a whole candidate line only when both readings agree on digit groups, numeric formatting (including percentages and signs), k/m price tokens, explicit trade tags, and the player's configured buying/selling classification. Otherwise it retains the regular reading.

A build can also embed an earlier trained model using `MapleChatFallbackModelPath`. This enables the evaluated two-model policy: accept a well-formed primary line at Tesseract mean confidence >= 0.90; otherwise read the earlier model and apply the original numeric/trade agreement guard against the regular reader. This allows an observed primary correction such as `1.6M` to `1.5M` or `5>` to `S>` instead of always preserving the original mistake. Each choice remains one complete observed line. Confidence is a selection heuristic, not a calibrated probability or proof of accuracy; agreeing models can also make the same mistake. EXP and loot keep using the regular engine. The earlier preview EXEs are unchanged.

This preparation is limited to the tested 1920×1080 compact gray/white bar: a crop 34–40 pixels high, with the text line at the training profile's inset. Unsupported crop heights, colored backgrounds, chat-history mode, absent regular readings, or missing preview assets use the regular reader. The extra model adds an OCR pass and CPU work; it is not a low-resource preset. Broader layouts and live message deduplication still need prospective gameplay evaluation.

Normal 1.1 builds use the trained models checked into `data/tessdata` and `data/tessdata-fallback`. To test another model, pass `MapleChatModelPath` (the integer candidate) and `MapleChatModelLicensePath` (its source model's license) as MSBuild properties. MapleTracker embeds the candidate as `maple_chat_int.traineddata`, verifies it on extraction, and credits the source model in the embedded notices. Run this from the repository root with your model paths:

```powershell
dotnet publish src/MapleTracker.App/MapleTracker.App.csproj -c Release -p:PublishProfile=Standalone -p:Version=1.0.2-ocr-preview.2 -p:MapleChatModelPath=C:/models/primary/maple_chat_int.traineddata -p:MapleChatFallbackModelPath=C:/models/previous/maple_chat_int.traineddata -p:MapleChatModelLicensePath=C:/models/LICENSE-tessdata_best
```

Only builds containing the model show **Setup → Experimental Maple font**. It defaults off. Interactive preview builds use `%LOCALAPPDATA%/MapleTracker-OCR-preview`, importing the stable profile's preferences once without changing its files or importing its journals. Subsequent preview launches preserve preview settings. Explicit `MAPLETRACKER_DATA_DIR` / AppContext overrides still take precedence for diagnostics and QA. CLI diagnostics use their requested output directory.

Replay the actual chat capture pipeline for each preset as well as benchmarking prepared lines:

```powershell
dotnet tools/MapleTracker.OcrBench/bin/Release/net10.0-windows/MapleTracker.OcrBench.dll replay C:/dataset/manifest.json C:/reports/validation-balanced.json C:/models Balanced validation C:/captures/original C:/captures/fresh
```

This developer replay uses the fixed default capture region and records each source hash, expected/current/selected line, model hashes, and warmed timing. It also records a separate direct candidate reading and its confidence. An optional final argument supplies the previous model directory for two-model-policy replay. Its manifest contains `fold`, `origin` (`original` or `fresh`), `source`, `normalized` (sample ID), and `text`; an explicit `source_path` overrides the two source directories for mixed corpora. Reports require a new filename. Use a matching source layout; this command does not infer a player's calibration.

The 629-capture round visually reviewed every capture and accepted 558 chat lines, excluding 71 ambiguous/clipped examples and system announcements. Sender identities, old split families, exact pixels/transcripts, and identical long numeric-normalized ad bodies were grouped before splitting. It added 444 training lines, 57 validation lines and 57 test lines; the earlier 100/23/30 split remained outside any new fitting leakage. The selected candidate was fine-tuned for 1,000 iterations using 544 full-line training examples. A second, weighted trial used 828 views (544 full lines, 84 old numeric crops, and 200 repeated old-line views) for 1,500 iterations; those extra views are not independent messages. The weighted trial failed the combined validation gates and was not selected. The two-model policy was selected using validation across all three presets before the final test was evaluated. Keep the resulting reports with the model: a successful training run alone is not permission to replace a reader.

The first numeric-focused preview used 100 independent corrected training messages plus 84 reviewed numeric crops derived only from those training messages. Its 23-line validation and 30-line test folds were not fitted, but had been examined in previous experiments. Treat their results as reused regression checks, not a new independent accuracy estimate. The trained model alone regressed on some critical fields, so it was not accepted as a direct replacement. The guarded combination must be judged separately against the current production pipeline and then tried on new gameplay.

For a bundled preview build, `MAPLETRACKER_PREVIEW_SMOKE_SCREENSHOT` enables an additional WPF check that requires a fixture with a readable HUD and populated compact chat bar. It verifies identical non-null HUD/loot results and the extra chat pass with the option on/off. Use an isolated self-test and screenshot verification on the published single EXE too.

Dataset, training-runner, and critical-field checks:

```powershell
python -m unittest discover -s scripts/ocr -p 'test_*.py' -v
```
