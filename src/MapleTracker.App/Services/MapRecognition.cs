using System.Drawing;
using System.Security.Cryptography;
using Tesseract;
using ImageFormat = System.Drawing.Imaging.ImageFormat;
using Color = System.Drawing.Color;

namespace MapleTracker.App.Services;

public sealed partial class OcrReader
{
    private string? mapPixels;
    private GameCatalog? cachedMapCatalog;
    private (string[] Text, MapReading? Match) cachedMap = ([], null);

    private (string[] Text, MapReading? Match) ReadMap(Bitmap frame, TrackerSettings settings, string? diagnostics, GameCatalog catalog)
    {
        var rectangle = settings.AutoMapRegion ? FindMapTitle(frame, settings.Map) : RegionPixels(frame, settings.Map);
        if (rectangle == null) { mapPixels = null; cachedMap = ([], null); return cachedMap; }
        using var crop = frame.Clone(rectangle.Value, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var memory = new MemoryStream(); crop.Save(memory, ImageFormat.Bmp);
        var hash = Convert.ToHexString(SHA256.HashData(memory.ToArray()));
        if (hash == mapPixels && ReferenceEquals(catalog, cachedMapCatalog) && diagnostics == null) return cachedMap;
        mapPixels = hash;
        cachedMapCatalog = catalog;
        var region = new CaptureRegion(rectangle.Value.X / (double)frame.Width, rectangle.Value.Y / (double)frame.Height,
            rectangle.Value.Width / (double)frame.Width, rectangle.Value.Height / (double)frame.Height);
        var candidates = new List<(string[] Text, MapReading? Match)>();
        foreach (int threshold in new[] { 165, 190, 220 })
        {
            var lines = ReadRegion(frame, region, "map-" + threshold, true, diagnostics, false, threshold, PageSegMode.SingleBlock);
            var text = lines.Select(l => l.Text).ToArray();
            var match = catalog.MatchMap(text);
            candidates.Add((text, match));
            if (match?.Confidence == 1) break;
        }
        if (!candidates.Any(c => c.Match?.Confidence == 1))
        {
            var text = ReadMapTextLines(crop, diagnostics);
            candidates.Add((text, catalog.MatchMap(text)));
        }
        cachedMap = candidates.OrderByDescending(c => c.Match?.Confidence ?? 0).FirstOrDefault(([], null));
        if (diagnostics != null)
        {
            Directory.CreateDirectory(diagnostics);
            File.WriteAllText(Path.Combine(diagnostics, "map.json"), System.Text.Json.JsonSerializer.Serialize(new
            { Region = rectangle, cachedMap.Text, cachedMap.Match, Candidates = candidates.Select(c => new { c.Text, c.Match }) }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        return cachedMap;
    }

    private string[] ReadMapTextLines(Bitmap crop, string? diagnostics)
    {
        const int threshold = 185;
        using var ink = new Bitmap(crop.Width, crop.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        var rows = new bool[crop.Height];
        for (int y = 0; y < crop.Height; y++)
        {
            int count = 0;
            for (int x = 0; x < crop.Width; x++)
            {
                var p = crop.GetPixel(x, y);
                bool white = Math.Min(p.R, Math.Min(p.G, p.B)) >= threshold && Math.Max(p.R, Math.Max(p.G, p.B)) - Math.Min(p.R, Math.Min(p.G, p.B)) < 75;
                ink.SetPixel(x, y, white ? Color.Black : Color.White);
                if (white) count++;
            }
            rows[y] = count >= 2 && count < crop.Width * .9;
        }
        var lines = new List<string>(); int top = -1;
        for (int y = 0; y <= crop.Height; y++)
        {
            if (y < crop.Height && rows[y]) { if (top < 0) top = y; continue; }
            if (top < 0) continue;
            int height = y - top;
            if (height >= 4)
            {
                int start = Math.Max(0, top - 3), end = Math.Min(crop.Height, y + 3);
                using var prepared = new Bitmap(crop.Width * 3 + 24, (end - start) * 3 + 24, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(prepared))
                {
                    g.Clear(Color.White);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    g.DrawImage(ink, new Rectangle(12, 12, crop.Width * 3, (end - start) * 3), new Rectangle(0, start, crop.Width, end - start), GraphicsUnit.Pixel);
                }
                if (diagnostics != null) prepared.Save(Path.Combine(diagnostics, $"map-line-{lines.Count}-ocr.png"));
                using var memory = new MemoryStream(); prepared.Save(memory, ImageFormat.Png);
                using var pix = Pix.LoadFromMemory(memory.ToArray());
                engine.SetVariable("tessedit_char_whitelist", "");
                using var page = engine.Process(pix, PageSegMode.SparseText);
                var text = page.GetText().Trim();
                if (text.Length >= 4) lines.Add(text);
            }
            top = -1;
        }
        return lines.ToArray();
    }

    private static Rectangle RegionPixels(Bitmap frame, CaptureRegion region)
    {
        int x = Math.Clamp((int)Math.Round(region.X * frame.Width), 0, frame.Width - 1);
        int y = Math.Clamp((int)Math.Round(region.Y * frame.Height), 0, frame.Height - 1);
        return new(x, y, Math.Clamp((int)Math.Round(region.Width * frame.Width), 1, frame.Width - x),
            Math.Clamp((int)Math.Round(region.Height * frame.Height), 1, frame.Height - y));
    }

    // The minimap body can change size. Locate its blue name panel independently.
    internal static Rectangle? FindMapTitle(Bitmap frame, CaptureRegion searchRegion)
    {
        var search = RegionPixels(frame, searchRegion);
        const int step = 2;
        int width = search.Width / step, height = search.Height / step;
        if (width < 30 || height < 10) return null;
        var mask = new bool[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            var p = frame.GetPixel(search.X + x * step, search.Y + y * step);
            mask[y * width + x] = p.B > p.R + 12 && p.G > p.R + 7 && p.B >= p.G && p.B - p.G < 55 && p.G > 90 && p.B < 245;
        }
        Rectangle? best = null; double bestScore = 0;
        var queue = new Queue<int>();
        double scale = frame.Width / 1920.0;
        for (int start = 0; start < mask.Length; start++)
        {
            if (!mask[start]) continue;
            mask[start] = false; queue.Enqueue(start);
            int left = start % width, right = left, top = start / width, bottom = top, count = 0;
            var rows = new int[height];
            while (queue.Count > 0)
            {
                int point = queue.Dequeue(), x = point % width, y = point / width; count++; rows[y]++;
                left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                void Visit(int nx, int ny)
                {
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) return;
                    int i = ny * width + nx;
                    if (mask[i]) { mask[i] = false; queue.Enqueue(i); }
                }
                Visit(x - 1, y); Visit(x + 1, y); Visit(x, y - 1); Visit(x, y + 1);
            }
            int w = (right - left + 1) * step;
            if (search.X + left * step > frame.Width * .08 || w < 120 * scale) continue;
            // Blue frame edges can connect the title to the whole minimap. Use the
            // dense horizontal band, excluding those thin edges and toolbar icons.
            int bandTop = -1, bandBottom = -1, runTop = -1;
            for (int y = top; y <= bottom + 1; y++)
            {
                if (y <= bottom && rows[y] >= (right - left + 1) * .5) { if (runTop < 0) runTop = y; }
                else if (runTop >= 0)
                {
                    if (y - runTop > bandBottom - bandTop) { bandTop = runTop; bandBottom = y - 1; }
                    runTop = -1;
                }
            }
            if (bandTop < 0) continue;
            top = bandTop; bottom = bandBottom;
            int h = (bottom - top + 1) * step;
            double density = rows.Skip(top).Take(bottom - top + 1).Sum() / (double)((right - left + 1) * (bottom - top + 1));
            if (h < 25 * scale || h > 100 * scale || density < .5) continue;
            int inset = Math.Max(24, (int)Math.Round(60 * scale));
            var title = new Rectangle(search.X + left * step + inset, search.Y + top * step + 2,
                w - inset - 2, h - 12);
            double score = w * h * density;
            if (title.Width > 60 && title.Height > 15 && score > bestScore) { best = title; bestScore = score; }
        }
        return best;
    }
}
