using System.Drawing;

namespace MapleTracker.App.Services;

public sealed partial class OcrReader
{
    private NotificationLine[] ReadLoot(Bitmap frame, CaptureRegion region, string? diagnostics)
    {
        var clear = ReadRegion(frame, region, "loot", true, diagnostics, thresholdOverride: 100);
        var faded = ReadRegion(frame, region, "loot-faded", true, diagnostics, thresholdOverride: 45);
        if (frame.Width != 1920 || frame.Height != 1080) return SelectLootLines(clear, faded);

        using var crop = WindowCapture.Crop(frame, region);
        int digitEnd = 1906 - (int)Math.Round(region.X * frame.Width);
        var visual = RuntimeAssets.MesoGlyphs.Read(crop, digitEnd);
        if (diagnostics != null)
            File.WriteAllText(Path.Combine(diagnostics, "loot-meso-glyphs.json"), System.Text.Json.JsonSerializer.Serialize(visual));
        if (visual.Length == 0) return SelectLootLines(clear, faded);

        // A recognized meso label with unclear digits must not become an OCR guess.
        var other = clear.Concat(faded).Where(l => !visual.Any(v => Math.Abs(v.Top - l.Top) < .055)).ToArray();
        return SelectLootLines(visual.Where(v => v.Line != null).Select(v => v.Line!).ToArray(), other);
    }
}
