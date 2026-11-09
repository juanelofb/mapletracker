using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using Tesseract;
using ImageFormat = System.Drawing.Imaging.ImageFormat;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace MapleTracker.App.Services;

public sealed record RecognitionFrame(string[] Chat, string[] Loot, string LevelText, string ExpText, HudReading? Hud)
{
    public NotificationLine[] LootLines { get; init; } = [];
    public HudCandidate[] HudCandidates { get; init; } = [];
}

public sealed class OcrReader : IDisposable
{
    private readonly TesseractEngine engine;
    private readonly ExperienceTable table;
    public OcrReader(ExperienceTable table)
    {
        this.table = table;
        var data = Path.Combine(AppContext.BaseDirectory, "tessdata");
        if (!File.Exists(Path.Combine(data, "eng.traineddata")))
            throw new FileNotFoundException("English OCR data is missing. Run scripts/Get-OcrData.ps1, then rebuild.");
        engine = new TesseractEngine(data, "eng", EngineMode.LstmOnly);
        engine.SetVariable("user_defined_dpi", "240");
    }

    public RecognitionFrame Read(Bitmap frame, TrackerSettings settings, string? diagnostics = null)
    {
        var chat = Region(frame, settings.Chat, "chat", false, diagnostics);
        var lootLines = ReadRegion(frame, settings.Loot, "loot", true, diagnostics);
        var loot = lootLines.Select(line => line.Text).ToArray();
        var level = string.Join(" ", Region(frame, settings.Level, "level", false, diagnostics, true));
        if (!int.TryParse(level, out var numericLevel) || numericLevel is < 1 or > 100)
            level = string.Join(" ", Region(frame, settings.Level, "level", false, diagnostics, true, 100, PageSegMode.SingleLine));
        var exp = "";
        HudReading? hud = null;
        var attempts = new List<object>();
        var candidates = new List<HudCandidate>();
        foreach (var (threshold, mode) in new[]
        {
            (100, PageSegMode.SingleLine), (0, PageSegMode.SingleLine),
            (150, PageSegMode.SingleWord), (150, PageSegMode.SingleLine),
            (175, PageSegMode.SingleWord), (75, PageSegMode.SingleLine)
        })
        {
            var lines = ReadRegion(frame, settings.Experience, "exp", false, diagnostics, true, threshold, mode);
            exp = string.Join(" ", lines.Select(line => line.Text));
            candidates.Add(new(exp, lines.Length > 0 ? lines.Average(line => line.Confidence) : 0));
            hud = TextParsing.Hud(level, exp, table);
            attempts.Add(new { threshold, Mode = mode.ToString(), Text = exp, Validated = hud != null });
            if (hud != null) break;
        }
        hud ??= HudConsensus.Read(level, candidates, table);
        var result = new RecognitionFrame(chat, loot, level, exp, hud) { LootLines = lootLines, HudCandidates = candidates.ToArray() };
        if (diagnostics != null)
        {
            File.WriteAllText(Path.Combine(diagnostics, "recognition.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(diagnostics, "hud-attempts.json"), JsonSerializer.Serialize(attempts, new JsonSerializerOptions { WriteIndented = true }));
        }
        return result;
    }

    private string[] Region(Bitmap frame, CaptureRegion region, string name, bool whiteText, string? diagnostics,
        bool singleLine = false, int? thresholdOverride = null, PageSegMode? segmentation = null)
        => ReadRegion(frame, region, name, whiteText, diagnostics, singleLine, thresholdOverride, segmentation).Select(line => line.Text).ToArray();

    private NotificationLine[] ReadRegion(Bitmap frame, CaptureRegion region, string name, bool whiteText, string? diagnostics,
        bool singleLine = false, int? thresholdOverride = null, PageSegMode? segmentation = null)
    {
        using var crop = WindowCapture.Crop(frame, region);
        var threshold = thresholdOverride ?? (singleLine ? 150 : 45);
        using var prepared = Prepare(crop, (whiteText || singleLine) && threshold > 0, threshold);
        if (diagnostics != null)
        {
            Directory.CreateDirectory(diagnostics);
            crop.Save(Path.Combine(diagnostics, name + "-original.png"));
            prepared.Save(Path.Combine(diagnostics, name + "-ocr.png"));
        }
        using var memory = new MemoryStream();
        prepared.Save(memory, ImageFormat.Png);
        using var pix = Pix.LoadFromMemory(memory.ToArray());
        engine.SetVariable("tessedit_char_whitelist", name == "level" ? "0123456789" : "");
        using var page = engine.Process(pix, segmentation ?? (singleLine ? PageSegMode.SingleWord : name == "loot" ? PageSegMode.SparseText : PageSegMode.Auto));
        var lines = new List<NotificationLine>();
        using var iter = page.GetIterator();
        iter.Begin();
        do
        {
            var text = iter.GetText(PageIteratorLevel.TextLine)?.Trim();
            if (!string.IsNullOrWhiteSpace(text) && (singleLine || text.Length >= 4 && iter.GetConfidence(PageIteratorLevel.TextLine) >= 35))
            {
                iter.TryGetBoundingBox(PageIteratorLevel.TextLine, out var bounds);
                var top = Math.Clamp((bounds.Y1 - 12) / 3.0 / crop.Height, 0, 1);
                var height = (bounds.Y2 - bounds.Y1) / 3.0 / crop.Height;
                var brightness = name == "loot" ? TextBrightness(crop, bounds) : 160;
                lines.Add(new(text, top, height, iter.GetConfidence(PageIteratorLevel.TextLine), brightness));
            }
        } while (iter.Next(PageIteratorLevel.TextLine));
        return lines.OrderBy(line => line.Top).ToArray();
    }

    private static double TextBrightness(Bitmap crop, Tesseract.Rect bounds)
    {
        var values = new List<int>();
        int left = Math.Clamp((bounds.X1 - 12) / 3, 0, crop.Width - 1);
        int top = Math.Clamp((bounds.Y1 - 12) / 3, 0, crop.Height - 1);
        int right = Math.Clamp((bounds.X2 - 12) / 3, left, crop.Width - 1);
        int bottom = Math.Clamp((bounds.Y2 - 12) / 3, top, crop.Height - 1);
        for (int y = top; y <= bottom; y++)
        for (int x = left; x <= right; x++)
        {
            var p = crop.GetPixel(x, y);
            int min = Math.Min(p.R, Math.Min(p.G, p.B));
            int max = Math.Max(p.R, Math.Max(p.G, p.B));
            if (min > 35 && max - min < 65) values.Add((p.R + p.G + p.B) / 3);
        }
        values.Sort();
        return values.Count == 0 ? 0 : values[(int)((values.Count - 1) * .8)];
    }

    public void Probe(Bitmap frame, TrackerSettings settings, string directory)
    {
        var results = new List<object>();
        foreach (var (name, region) in new[] { ("level", settings.Level), ("exp", settings.Experience) })
        {
            using var crop = WindowCapture.Crop(frame, region);
            foreach (var threshold in new[] { 0, 100, 150, 175 })
            {
                using var prepared = Prepare(crop, threshold > 0, threshold);
                using var stream = new MemoryStream(); prepared.Save(stream, ImageFormat.Png);
                using var pix = Pix.LoadFromMemory(stream.ToArray());
                foreach (var mode in new[] { PageSegMode.SingleLine, PageSegMode.SingleWord, PageSegMode.RawLine, PageSegMode.SingleBlock })
                foreach (var whitelist in new[] { "", name == "level" ? "0123456789" : "0123456789.,[]()%" })
                {
                    engine.SetVariable("tessedit_char_whitelist", whitelist);
                    using var page = engine.Process(pix, mode);
                    results.Add(new { name, threshold, mode = mode.ToString(), whitelist, text = page.GetText(), confidence = page.GetMeanConfidence() });
                }
            }
        }
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "probe.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static unsafe Bitmap Prepare(Bitmap image, bool whiteText, int threshold)
    {
        var output = new Bitmap(image.Width * 3 + 24, image.Height * 3 + 24, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(output))
        {
            g.Clear(System.Drawing.Color.Black);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(image, new Rectangle(12, 12, image.Width * 3, image.Height * 3));
        }
        var data = output.LockBits(new Rectangle(0, 0, output.Width, output.Height), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
            for (var y = 0; y < output.Height; y++)
            {
                var row = (byte*)data.Scan0 + y * data.Stride;
                for (var x = 0; x < output.Width; x++)
                {
                    var p = row + x * 3;
                    if (whiteText)
                    {
                        var min = Math.Min(p[0], Math.Min(p[1], p[2]));
                        var max = Math.Max(p[0], Math.Max(p[1], p[2]));
                        var value = (byte)(min >= threshold && max - min < 75 ? 0 : 255);
                        p[0] = p[1] = p[2] = value;
                    }
                    else { p[0] = (byte)(255 - p[0]); p[1] = (byte)(255 - p[1]); p[2] = (byte)(255 - p[2]); }
                }
            }
        }
        finally { output.UnlockBits(data); }
        return output;
    }
    public void Dispose() => engine.Dispose();
}
