using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MapleTracker.App.Services;
using Tesseract;
using ImageFormat = System.Drawing.Imaging.ImageFormat;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace MapleTracker.OcrBench;

internal static class NumericSamples
{
    public static int Extract(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Usage: OcrBench numeric-samples <training-line-directory> <new-output-directory>");
        var source = Path.GetFullPath(args[0]);
        var output = Path.GetFullPath(args[1]);
        if (Directory.Exists(output)) throw new IOException("Output already exists.");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(Path.Combine(output, "samples"));
        AppContext.SetData("MapleTracker.DataDirectory", Path.Combine(output, ".runtime"));
        RuntimeAssets.PrepareNativeOcr();
        using var engine = new TesseractEngine(RuntimeAssets.OcrDataDirectory, "eng", EngineMode.LstmOnly);
        engine.SetVariable("user_defined_dpi", "240");
        var pattern = new Regex(@"^[+\-]?\d+(?:[.,]\d+)?(?:[kKmM%]|[mM][iI][lL])?$", RegexOptions.CultureInvariant);
        var references = new Regex(@"(?<![\w.,])[+\-]?\d+(?:[.,]\d+)?(?:[kKmM%]|[mM][iI][lL])?(?![\w.,])", RegexOptions.CultureInvariant);
        var records = new List<object>();
        var tiles = new List<(string Id, string Label)>();
        var seen = new HashSet<string>();
        foreach (var image in Directory.GetFiles(source, "sample-*.png").OrderBy(p => p, StringComparer.Ordinal))
        {
            var expected = File.ReadAllText(Path.ChangeExtension(image, ".gt.txt")).Trim();
            var values = references.Matches(expected).Select(m => m.Value).ToArray();
            using var bitmap = new Bitmap(image);
            using var pix = Pix.LoadFromFile(image);
            using var page = engine.Process(pix, PageSegMode.SingleLine);
            using var iterator = page.GetIterator();
            iterator.Begin();
            do
            {
                var word = iterator.GetText(PageIteratorLevel.Word)?.Trim();
                if (word == null || !pattern.IsMatch(word)) continue;
                var target = values.FirstOrDefault(v => v == word) ?? values.FirstOrDefault(v => v.Equals(word, StringComparison.OrdinalIgnoreCase));
                if (target == null || !iterator.TryGetBoundingBox(PageIteratorLevel.Word, out var bounds)) continue;
                var rectangle = Rectangle.Intersect(new Rectangle(bounds.X1 - 3, bounds.Y1 - 3, bounds.Width + 6, bounds.Height + 6), new Rectangle(0,0,bitmap.Width,bitmap.Height));
                if (rectangle.Width < 1 || rectangle.Height < 1) continue;
                using var cropped = new Bitmap(rectangle.Width + 24, rectangle.Height + 24, PixelFormat.Format24bppRgb);
                using (var graphics = Graphics.FromImage(cropped))
                {
                    graphics.Clear(Color.White);
                    graphics.DrawImage(bitmap, new Rectangle(12,12,rectangle.Width,rectangle.Height), rectangle, GraphicsUnit.Pixel);
                }
                using var memory = new MemoryStream();
                cropped.Save(memory, ImageFormat.Png);
                var bytes = memory.ToArray();
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!seen.Add(hash)) continue;
                var id = "sample-" + hash[..20];
                File.WriteAllBytes(Path.Combine(output,"samples",id+".png"), bytes);
                File.WriteAllText(Path.Combine(output,"samples",id+".gt.txt"),target+"\n");
                records.Add(new { Id=id, SourceId=Path.GetFileNameWithoutExtension(image), Text=target,
                    Rectangle=new {rectangle.X,rectangle.Y,rectangle.Width,rectangle.Height}, Sha256=hash, Reviewed=false });
                tiles.Add((id,target));
            } while (iterator.Next(PageIteratorLevel.Word));
        }
        File.WriteAllText(Path.Combine(output,"candidates.json"),JsonSerializer.Serialize(records,new JsonSerializerOptions { WriteIndented=true }));
        for (var first=0;first<tiles.Count;first+=64)
        {
            var batch=tiles.Skip(first).Take(64).ToArray();
            using var sheet=new Bitmap(1600,((batch.Length+3)/4)*110,PixelFormat.Format24bppRgb);
            using var graphics=Graphics.FromImage(sheet);
            graphics.Clear(Color.FromArgb(22,27,36));
            using var font=new Font("Segoe UI",11,FontStyle.Bold);
            for (var n=0;n<batch.Length;n++)
            {
                var x=n%4*400;var y=n/4*110;
                graphics.DrawString($"{first+n+1:D3}  target: {batch[n].Label}",font,Brushes.White,x+5,y+1);
                using var crop=new Bitmap(Path.Combine(output,"samples",batch[n].Id+".png"));
                graphics.DrawImageUnscaled(crop,x+5,y+23);
            }
            sheet.Save(Path.Combine(output,$"review-{first/64+1:D2}.png"),ImageFormat.Png);
        }
        Console.WriteLine($"Prepared {records.Count} numeric-word candidates from training lines only. Review the sheets before using them.");
        return 0;
    }
}
