using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using MapleTracker.App.Services;
using Tesseract;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using ImageFormat = System.Drawing.Imaging.ImageFormat;

namespace MapleTracker.OcrBench;

internal static class ScreenshotCorpus
{
    public static int Normalize(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Usage: OcrBench normalize <raw-line-directory> <new-output-directory>");
        var source = Path.GetFullPath(args[0]);
        var output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output)) throw new IOException("Output already exists.");
        Directory.CreateDirectory(output);
        var files = Directory.GetFiles(source, "*.png").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        foreach (var file in files)
        {
            using var raw = new Bitmap(file);
            if (raw.Width != 739 || raw.Height != 37) throw new IOException("Unexpected raw chat crop dimensions.");
            using var line = raw.Clone(new Rectangle(10, 6, 729, 25), PixelFormat.Format24bppRgb);
            var background = line.GetPixel(line.Width - 2, 1);
            for (var y = 0; y < line.Height; y++)
            for (var x = 0; x < line.Width; x++)
            {
                var p = line.GetPixel(x, y);
                var contrast = Math.Max(Math.Abs(p.R - background.R), Math.Max(Math.Abs(p.G - background.G), Math.Abs(p.B - background.B)));
                line.SetPixel(x, y, contrast >= 30 ? Color.Black : Color.White);
            }
            using var prepared = new Bitmap(line.Width * 3 + 24, line.Height * 3 + 24, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(prepared))
            {
                graphics.Clear(Color.White);
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                graphics.DrawImage(line, new Rectangle(12, 12, line.Width * 3, line.Height * 3));
            }
            prepared.Save(Path.Combine(output, Path.GetFileName(file)), ImageFormat.Png);
        }
        Console.WriteLine($"Normalized {files.Length} crops to black text on white, preserving the original images.");
        return 0;
    }

    public static int Extract(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Usage: OcrBench extract <game-screenshot-directory> <new-output-directory>");
        var source = Path.GetFullPath(args[0]);
        var output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output)) throw new IOException("Output directory already exists; use a new corpus snapshot.");
        var files = Directory.GetFiles(source).Where(f => new[] { ".jpg", ".jpeg", ".png" }.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        if (files.Length == 0) throw new IOException("No JPG or PNG screenshots found.");
        Directory.CreateDirectory(Path.Combine(output, "prepared"));
        Directory.CreateDirectory(Path.Combine(output, "raw"));
        Directory.CreateDirectory(Path.Combine(output, "sheets"));
        AppContext.SetData("MapleTracker.DataDirectory", Path.Combine(output, ".runtime"));
        RuntimeAssets.PrepareNativeOcr();
        using var engine = new TesseractEngine(RuntimeAssets.OcrDataDirectory, "eng", EngineMode.LstmOnly);
        engine.SetVariable("user_defined_dpi", "240");
        var entries = new List<object>();
        var crops = new List<(int Index, string File, string Path)>();
        for (var index = 0; index < files.Length; index++)
        {
            using var frame = new Bitmap(files[index]);
            if (frame.Width != 1920 || frame.Height != 1080) throw new IOException("This capture profile requires 1920x1080 screenshots.");
            // The player's compact chat region: same 3x nearest-neighbor inversion as runtime pass zero.
            using var raw = frame.Clone(new Rectangle(403, 989, 739, 37), PixelFormat.Format24bppRgb);
            using var prepared = new Bitmap(raw.Width * 3 + 24, raw.Height * 3 + 24, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(prepared))
            {
                graphics.Clear(Color.Black);
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                graphics.DrawImage(raw, new Rectangle(12, 12, raw.Width * 3, raw.Height * 3));
            }
            Invert(prepared);
            var number = (index + 1).ToString("D3");
            var rawPath = Path.Combine(output, "raw", number + ".png");
            var preparedPath = Path.Combine(output, "prepared", number + ".png");
            raw.Save(rawPath, ImageFormat.Png);
            prepared.Save(preparedPath, ImageFormat.Png);
            using var pix = Pix.LoadFromFile(preparedPath);
            using var page = engine.Process(pix, PageSegMode.SingleLine);
            var text = page.GetText().Trim();
            entries.Add(new
            {
                Index = index + 1, Source = Path.GetFileName(files[index]),
                SourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(files[index]))).ToLowerInvariant(),
                PreparedSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(preparedPath))).ToLowerInvariant(),
                ProposedText = text, Confidence = page.GetMeanConfidence(), Reviewed = false
            });
            crops.Add((index + 1, Path.GetFileName(files[index]), rawPath));
            if ((index + 1) % 25 == 0) Console.WriteLine($"Extracted {index + 1}/{files.Length}");
        }
        File.WriteAllText(Path.Combine(output, "candidates.json"), JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
        for (var first = 0; first < crops.Count; first += 16)
        {
            var batch = crops.Skip(first).Take(16).ToArray();
            using var sheet = new Bitmap(1502, batch.Length * 100, PixelFormat.Format24bppRgb);
            using var graphics = Graphics.FromImage(sheet);
            graphics.Clear(Color.FromArgb(22, 27, 36));
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            using var font = new Font("Segoe UI", 12, FontStyle.Bold);
            for (var row = 0; row < batch.Length; row++)
            {
                var entry = batch[row];
                graphics.DrawString($"{entry.Index:D3}  {entry.File}", font, Brushes.White, 12, row * 100);
                using var crop = new Bitmap(entry.Path);
                graphics.DrawImage(crop, new Rectangle(12, row * 100 + 24, crop.Width * 2, crop.Height * 2));
            }
            sheet.Save(Path.Combine(output, "sheets", $"sheet-{first / 16 + 1:D2}.png"), ImageFormat.Png);
        }
        Console.WriteLine($"Prepared {files.Length} unreviewed candidates and contact sheets. Sources were read only.");
        return 0;
    }

    private static unsafe void Invert(Bitmap image)
    {
        var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
            for (var y = 0; y < image.Height; y++)
            {
                var row = (byte*)data.Scan0 + y * data.Stride;
                for (var x = 0; x < image.Width * 3; x++) row[x] = (byte)(255 - row[x]);
            }
        }
        finally { image.UnlockBits(data); }
    }
}
