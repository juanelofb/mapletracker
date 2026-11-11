using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using MapleTracker.App.Services;
using MapleTracker.Core;
using Tesseract;
using ImageFormat = System.Drawing.Imaging.ImageFormat;

namespace MapleTracker.OcrBench;

internal static class ScreenshotReplay
{
    public static int Run(string[] args)
    {
        if (args.Length is < 7 or > 8) throw new ArgumentException("Usage: OcrBench replay <manifest.json> <new-report.json> <model-directory> <Low|Balanced|High> <validation|test> <original-screenshot-directory> <fresh-screenshot-directory> [fallback-model-directory]");
        var output=Path.GetFullPath(args[1]);
        if(File.Exists(output)) throw new IOException("Report already exists.");
        var quality=Enum.Parse<RecognitionQuality>(args[3]);
        using var manifest=JsonDocument.Parse(File.ReadAllText(args[0]));
        var rows=manifest.RootElement.EnumerateArray().Where(r=>r.GetProperty("fold").GetString()==args[4]).ToArray();
        if(rows.Length==0) throw new IOException("No records in that fold.");
        AppContext.SetData("MapleTracker.DataDirectory",Path.Combine(Path.GetDirectoryName(output)!,".replay-runtime"));
        var table=ExperienceTable.Load(RuntimeAssets.ExperienceTablePath);
        using var baseline=new OcrReader(table);
        using var experimental=new OcrReader(table,Path.GetFullPath(args[2]),args.Length==8?Path.GetFullPath(args[7]):null);
        using var direct=new TesseractEngine(Path.GetFullPath(args[2]),"maple_chat_int",EngineMode.LstmOnly);
        direct.SetVariable("user_defined_dpi","240");
        var currentSettings=new TrackerSettings { RecognitionQuality=quality };
        var experimentalSettings=new TrackerSettings { RecognitionQuality=quality, UseExperimentalChatFont=true };
        var readings=new List<object>();
        foreach(var row in rows)
        {
            var file=row.TryGetProperty("source_path",out var explicitPath) ? explicitPath.GetString()! :
                Path.Combine(row.GetProperty("origin").GetString()=="original"?args[5]:args[6],row.GetProperty("source").GetString()!);
            using var image=new Bitmap(file);
            if(readings.Count==0)
            {
                baseline.ReadChat(image,currentSettings,null);
                experimental.ReadChat(image,experimentalSettings,null);
            }
            var clock=Stopwatch.StartNew();
            var current=string.Join("\n",baseline.ReadChat(image,currentSettings,null));
            clock.Stop();var currentMs=clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            var guarded=string.Join("\n",experimental.ReadChat(image,experimentalSettings,null));
            clock.Stop();
            var guardedMs=clock.Elapsed.TotalMilliseconds;
            string? raw=null;float? confidence=null;
            using(var crop=WindowCapture.Crop(image,currentSettings.Chat))
            using(var prepared=OcrReader.PrepareExperimentalChat(crop))
            {
                if(prepared!=null)
                {
                    using var memory=new MemoryStream();prepared.Save(memory,ImageFormat.Png);
                    using var pix=Pix.LoadFromMemory(memory.ToArray());
                    using var page=direct.Process(pix,PageSegMode.SingleLine);
                    raw=page.GetText().Trim();confidence=page.GetMeanConfidence();
                }
            }
            readings.Add(new { Id=row.GetProperty("normalized").GetString(),Source=Path.GetFileName(file),
                SourceSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant(),
                Expected=row.GetProperty("text").GetString(),Current=current,Guarded=guarded,
                Direct=raw,DirectConfidence=confidence,
                CurrentMilliseconds=currentMs,GuardedMilliseconds=guardedMs });
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output,JsonSerializer.Serialize(new { Quality=quality.ToString(),Fold=args[4],
            ModelSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(args[2],"maple_chat_int.traineddata")))).ToLowerInvariant(),
            FallbackModelSha256=args.Length==8?Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(args[7],"maple_chat_int.traineddata")))).ToLowerInvariant():null,
            DefaultCaptureRegion=currentSettings.Chat,Readings=readings },new JsonSerializerOptions { WriteIndented=true }));
        Console.WriteLine($"Replayed {readings.Count} full screenshots at {quality} with the actual chat capture pipeline.");
        return 0;
    }
}
