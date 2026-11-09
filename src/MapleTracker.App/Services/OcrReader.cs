using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using Tesseract;
using ImageFormat = System.Drawing.Imaging.ImageFormat;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace MapleTracker.App.Services;

public sealed record RecognitionFrame(string[] Chat, string[] Loot, string LevelText, string ExpText, HudReading? Hud);

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
        var loot = Region(frame, settings.Loot, "loot", true, diagnostics);
        var level = string.Join(" ", Region(frame, settings.Level, "level", false, diagnostics, true));
        var exp = string.Join(" ", Region(frame, settings.Experience, "exp", false, diagnostics, true));
        var result = new RecognitionFrame(chat, loot, level, exp, TextParsing.Hud(level, exp, table));
        if (diagnostics != null)
            File.WriteAllText(Path.Combine(diagnostics, "recognition.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result;
    }

    private string[] Region(Bitmap frame, CaptureRegion region, string name, bool whiteText, string? diagnostics, bool singleLine = false)
    {
        using var crop = WindowCapture.Crop(frame, region);
        using var prepared = Prepare(crop, whiteText || singleLine, name == "level" ? 150 : name == "exp" ? 150 : 45);
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
        using var page = engine.Process(pix, singleLine ? PageSegMode.SingleWord : name == "loot" ? PageSegMode.SparseText : PageSegMode.Auto);
        var lines = new List<(int Y, int X, string Text)>();
        using var iter = page.GetIterator();
        iter.Begin();
        do
        {
            var text = iter.GetText(PageIteratorLevel.TextLine)?.Trim();
            if (!string.IsNullOrWhiteSpace(text) && (singleLine || text.Length >= 4 && iter.GetConfidence(PageIteratorLevel.TextLine) >= 35))
            {
                iter.TryGetBoundingBox(PageIteratorLevel.TextLine, out var bounds);
                lines.Add((bounds.Y1, bounds.X1, text));
            }
        } while (iter.Next(PageIteratorLevel.TextLine));
        return lines.OrderBy(x => x.Y).ThenBy(x => x.X).Select(x => x.Text).ToArray();
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
