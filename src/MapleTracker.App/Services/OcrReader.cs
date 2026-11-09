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
    public double OcrMilliseconds { get; init; }
    public int ChatPasses { get; init; }
}

public sealed partial class OcrReader : IDisposable
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
        var clock = System.Diagnostics.Stopwatch.StartNew();
        chatPasses = 0;
        var chat = ReadChat(frame, settings, diagnostics);
        var clearLoot = ReadRegion(frame, settings.Loot, "loot", true, diagnostics, thresholdOverride: 100);
        var fadedLoot = ReadRegion(frame, settings.Loot, "loot-faded", true, diagnostics, thresholdOverride: 45);
        var lootLines = SelectLootLines(clearLoot, fadedLoot);
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
        var result = new RecognitionFrame(chat, loot, level, exp, hud) { LootLines = lootLines, HudCandidates = candidates.ToArray(), OcrMilliseconds = clock.Elapsed.TotalMilliseconds, ChatPasses = chatPasses };
        if (diagnostics != null)
        {
            File.WriteAllText(Path.Combine(diagnostics, "recognition.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(diagnostics, "hud-attempts.json"), JsonSerializer.Serialize(attempts, new JsonSerializerOptions { WriteIndented = true }));
        }
        return result;
    }

    internal static NotificationLine[] SelectLootLines(NotificationLine[] clear, NotificationLine[] faded)
    {
        var selected = new List<NotificationLine>();
        // OCR passes are alternative readings of one frame, never additional sightings.
        foreach (var line in clear.Concat(faded))
        {
            if (line.Confidence < 55 || TextParsing.Loot(line.Text) == null && TextParsing.Mesos(line.Text) == null) continue;
            if (selected.Any(other => Math.Abs(other.Top - line.Top) < Math.Max(.05, Math.Min(other.Height, line.Height) * .45))) continue;
            selected.Add(line);
        }
        return selected.OrderBy(line => line.Top).ToArray();
    }

    private string[] Region(Bitmap frame, CaptureRegion region, string name, bool whiteText, string? diagnostics,
        bool singleLine = false, int? thresholdOverride = null, PageSegMode? segmentation = null)
        => ReadRegion(frame, region, name, whiteText, diagnostics, singleLine, thresholdOverride, segmentation).Select(line => line.Text).ToArray();

    private NotificationLine[] ReadRegion(Bitmap frame, CaptureRegion region, string name, bool whiteText, string? diagnostics,
        bool singleLine = false, int? thresholdOverride = null, PageSegMode? segmentation = null, bool grayscale = false, bool banner = false, bool maskBadge = true)
    {
        using var crop = WindowCapture.Crop(frame, region);
        var threshold = thresholdOverride ?? (singleLine ? 150 : 45);
        using var prepared = Prepare(crop, (whiteText || singleLine) && threshold > 0, threshold, grayscale, banner, maskBadge);
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
        using var page = engine.Process(pix, segmentation ?? (singleLine ? PageSegMode.SingleWord : name.StartsWith("loot", StringComparison.Ordinal) ? PageSegMode.SparseText : PageSegMode.Auto));
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
                var brightness = name.StartsWith("loot", StringComparison.Ordinal) ? TextBrightness(crop, bounds) : 160;
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
        results.Add(new { name = "banner", lines = ReadRegion(frame, settings.Chat, "chat", false, directory, false, 0, PageSegMode.SingleLine, false, true) });
        foreach (var (name, region) in new[] { ("chat", settings.Chat), ("loot", settings.Loot) })
        foreach (var threshold in new[] { 0, 100, 150, 180 })
        foreach (var mode in new[] { PageSegMode.SingleLine, PageSegMode.SingleBlock, PageSegMode.SparseText })
        foreach (var gray in new[] { false, true })
        {
            var lines = ReadRegion(frame, region, name, true, null, false, threshold, mode, gray);
            results.Add(new { name, threshold, mode = mode.ToString(), gray, lines });
        }
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

    private static unsafe Bitmap Prepare(Bitmap image, bool whiteText, int threshold, bool grayscale = false, bool banner = false, bool maskBadge = true)
    {
        var background = banner ? BannerBackground(image) : null;
        grayscale |= background is { } color && color.B > color.G * .5;
        var output = new Bitmap(image.Width * 3 + 24, image.Height * 3 + 24, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(output))
        {
            g.Clear(grayscale || background != null ? System.Drawing.Color.White : System.Drawing.Color.Black);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(image, new Rectangle(12, 12, image.Width * 3, image.Height * 3));
            if (maskBadge && background is { } fill)
            {
                using var brush = new SolidBrush(fill);
                foreach (var badge in ChannelBadges.Find(image))
                    g.FillRectangle(brush, 12 + badge.X * 3, 12 + badge.Y * 3, badge.Width * 3, badge.Height * 3);
            }
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
                    if (!grayscale && background is { } bg)
                    {
                        var max = Math.Max(p[0], Math.Max(p[1], p[2]));
                        // Dark banner text and white item links need the same ink color.
                        // Ignore the black outside border and the gold/pink fill.
                        var ink = x >= 24 && y >= 21 && x < output.Width - 24 && y < output.Height - 24 &&
                            (max >= 35 && p[2] < bg.R - 45 && p[1] < bg.G - 35 ||
                             p[1] > bg.G + 35 && p[0] > bg.B + 60);
                        p[0] = p[1] = p[2] = (byte)(ink ? 0 : 255);
                    }
                    else if (grayscale)
                    {
                        p[0] = p[1] = p[2] = (byte)((p[2] * 299 + p[1] * 587 + p[0] * 114) / 1000);
                    }
                    else if (whiteText)
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
    private static System.Drawing.Color? BannerBackground(Bitmap image)
    {
        var buckets = new Dictionary<int, (int Count, System.Drawing.Color Color)>();
        for (int y = 2; y < image.Height - 2; y += 2)
        for (int x = 2; x < image.Width - 2; x += 3)
        {
            var p = image.GetPixel(x, y);
            int key = (p.R / 16 << 8) | (p.G / 16 << 4) | p.B / 16;
            var value = buckets.GetValueOrDefault(key); buckets[key] = (value.Count + 1, p);
        }
        if (buckets.Count == 0) return null;
        var most = buckets.Values.MaxBy(v => v.Count);
        var c = most.Color;
        return most.Count > image.Width * image.Height / 6.0 * .2 && Math.Max(c.R, Math.Max(c.G, c.B)) > 110 &&
            Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) > 30 ? c : null;
    }
    public void Dispose() => engine.Dispose();
}
