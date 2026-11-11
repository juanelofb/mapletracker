using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MapleTracker.App.Services;
using MapleTracker.Core;

namespace MapleTracker.OcrBench;

// Exercises the application's own visible-client capture path. No input or game-memory access.
internal static class LootReplay
{
    public static int Replay(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("replay-loot <recorded-directory> <report.json>");
        var directory = Path.GetFullPath(args[0]); var output = Path.GetFullPath(args[1]);
        AppContext.SetData("MapleTracker.DataDirectory", Path.Combine(Path.GetDirectoryName(output)!, "replay-app-data"));
        var paths = Directory.GetFiles(directory, "game-*.png").Where(p => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(p), @"^game-\d{4}\.png$")).Order().ToArray();
        using var reader = new OcrReader(ExperienceTable.Load(RuntimeAssets.ExperienceTablePath));
        var feed = new LootFeedTracker(); var rows = new List<object>(); long total = 0;
        DateTimeOffset? start = null;
        foreach (var path in paths)
        {
            using var original = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(path, ".json")));
            var time = original.RootElement.GetProperty("Time").GetDateTimeOffset(); start ??= time;
            using var image = new System.Drawing.Bitmap(path);
            var frame = reader.Read(image, new TrackerSettings());
            var added = feed.Update(frame.LootLines, time - start.Value);
            total += added.Sum(text => TextParsing.Mesos(text) ?? 0);
            rows.Add(new { File = Path.GetFileName(path), Time = time, frame.LootLines, Added = added, TotalMesos = total, frame.OcrMilliseconds });
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { Frames = rows, TotalMesos = total }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Replayed {rows.Count} frames; confirmed meso total {total}."); return 0;
    }

    public static int Record(string[] args)
    {
        if (args.Length is < 2 or > 3) throw new ArgumentException("record-loot <output-directory> <settings.json> [seconds=15]");
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        AppContext.SetData("MapleTracker.DataDirectory", Path.Combine(output, "app-data"));
        var settings = JsonSerializer.Deserialize<TrackerSettings>(File.ReadAllText(args[1]))!;
        var windows = WindowCapture.List().Where(w => w.Title == settings.WindowTitle).ToArray();
        if (windows.Length != 1) throw new InvalidOperationException("Expected one game window with the saved title.");
        int seconds = args.Length == 3 ? Math.Clamp(int.Parse(args[2]), 1, 60) : 15;
        var clock = Stopwatch.StartNew(); int count = 0;
        using var reader = new OcrReader(ExperienceTable.Load(RuntimeAssets.ExperienceTablePath));
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            if (WindowCapture.GetForegroundWindow() != windows[0].Handle || WindowCapture.IsIconic(windows[0].Handle) || WindowCapture.RegionsCovered(windows[0].Handle, settings)) { Thread.Sleep(100); continue; }
            using var bitmap = WindowCapture.Capture(windows[0].Handle);
            string name = Path.Combine(output, $"game-{count++:D4}");
            bitmap.Save(name + ".png", ImageFormat.Png);
            var frame = reader.Read(bitmap, settings);
            File.WriteAllText(name + ".json", JsonSerializer.Serialize(new { Time = DateTimeOffset.Now, Frame = frame }, new JsonSerializerOptions { WriteIndented = true }));
            Thread.Sleep(150);
        }
        Console.WriteLine($"Recorded {count} lossless client frames for local OCR replay.");
        return 0;
    }
}
