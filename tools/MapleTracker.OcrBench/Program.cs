using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MapleTracker.App.Services;
using Tesseract;

namespace MapleTracker.OcrBench;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "extract") return ScreenshotCorpus.Extract(args.Skip(1).ToArray());
            if (args.Length > 0 && args[0] == "normalize") return ScreenshotCorpus.Normalize(args.Skip(1).ToArray());
            if (args.Length > 0 && args[0] == "numeric-samples") return NumericSamples.Extract(args.Skip(1).ToArray());
            if (args.Length > 0 && args[0] == "replay") return ScreenshotReplay.Run(args.Skip(1).ToArray());
            if (args.Length > 0 && args[0] == "record-loot") return LootReplay.Record(args.Skip(1).ToArray());
            if (args.Length > 0 && args[0] == "replay-loot") return LootReplay.Replay(args.Skip(1).ToArray());
            if (args.Length is < 3 or > 5)
                throw new ArgumentException("Usage: OcrBench <prepared-line-directory> <output.json> <bundled|tessdata-directory> [language=eng] [singleline|rawline]");
            var dataset = Path.GetFullPath(args[0]);
            var output = Path.GetFullPath(args[1]);
            var language = args.Length >= 4 ? args[3] : "eng";
            if (!Regex.IsMatch(language, @"^[A-Za-z0-9_]+$")) throw new ArgumentException("Use one model name, without a path or extension.");
            var mode = args.Length < 5 || args[4] == "singleline" ? PageSegMode.SingleLine
                : args[4] == "rawline" ? PageSegMode.RawLine : throw new ArgumentException("Unknown segmentation mode.");
            if (File.Exists(output)) throw new IOException("Prediction file already exists; use a new report name.");
            var images = Directory.GetFiles(dataset, "sample-*.png").OrderBy(p => p, StringComparer.Ordinal).ToArray();
            if (images.Length == 0) throw new IOException("Dataset has no line images.");
            foreach (var image in images)
                if (!File.Exists(Path.ChangeExtension(image, ".gt.txt"))) throw new IOException("Missing transcript for " + image);
            // Isolate native extraction and all app-data access from the player's saved settings.
            AppContext.SetData("MapleTracker.DataDirectory", Path.Combine(Path.GetDirectoryName(output)!, ".ocrbench-runtime"));
            RuntimeAssets.PrepareNativeOcr();
            var modelDirectory = args[2] == "bundled" ? RuntimeAssets.OcrDataDirectory : Path.GetFullPath(args[2]);
            var modelPath = Path.Combine(modelDirectory, language + ".traineddata");
            var modelHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(modelPath))).ToLowerInvariant();
            using var engine = new TesseractEngine(modelDirectory, language, EngineMode.LstmOnly);
            engine.SetVariable("user_defined_dpi", "240");
            using (var pix = Pix.LoadFromFile(images[0]))
            using (var warmup = engine.Process(pix, mode)) { _ = warmup.GetText(); }
            var results = new List<object>();
            foreach (var image in images)
            {
                // Prepared pixels are shared unchanged by baseline and candidate models.
                using var pix = Pix.LoadFromFile(image);
                var clock = Stopwatch.StartNew();
                using var page = engine.Process(pix, mode);
                var text = page.GetText().Trim();
                clock.Stop();
                results.Add(new
                {
                    Id = Path.GetFileNameWithoutExtension(image), Text = text,
                    ImageSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(image))).ToLowerInvariant(),
                    Milliseconds = clock.Elapsed.TotalMilliseconds, Confidence = page.GetMeanConfidence()
                });
            }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(new
            {
                Model = language, ModelSha256 = modelHash, Psm = mode.ToString(),
                Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                WarmupExcluded = true, Results = results
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Read {results.Count} lines using {language} ({mode}). Report: {output}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}
