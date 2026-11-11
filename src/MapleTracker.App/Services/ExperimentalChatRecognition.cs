using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Tesseract;
using ImageFormat = System.Drawing.Imaging.ImageFormat;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using Color = System.Drawing.Color;

namespace MapleTracker.App.Services;

public sealed partial class OcrReader
{
    private string[] ReadExperimentalChat(Bitmap crop, string[] current, TrackerSettings settings, string? diagnostics)
    {
        var hasFallback = experimentalFallbackDirectory != null || experimentalModelDirectory == null && RuntimeAssets.HasExperimentalFallbackModel;
        if (!settings.UseExperimentalChatFont || current.Length > 1 || current.Length == 0 && !hasFallback || crop.Width < 200 ||
            crop.Height is < 34 or > 40 || experimentalModelDirectory == null && !RuntimeAssets.HasExperimentalChatModel) return current;
        using var prepared = PrepareExperimentalChat(crop);
        if (prepared == null) return current;
        experimentalChatEngine ??= new TesseractEngine(experimentalModelDirectory ?? RuntimeAssets.ExperimentalChatDataDirectory, "maple_chat_int", EngineMode.LstmOnly);
        experimentalChatEngine.SetVariable("user_defined_dpi", "240");
        using var memory = new MemoryStream();
        prepared.Save(memory, ImageFormat.Png);
        using var pix = Pix.LoadFromMemory(memory.ToArray());
        var (candidate, confidence) = ReadFontModel(experimentalChatEngine, pix);
        chatPasses++;
        var regular = current.FirstOrDefault() ?? "";
        string? fallback = null; float fallbackConfidence = 0;
        string chosen;
        if (hasFallback)
        {
            if (!ChatReadingGuard.CanUsePrimary(candidate, confidence))
            {
                experimentalFallbackEngine ??= new TesseractEngine(experimentalFallbackDirectory ?? RuntimeAssets.ExperimentalFallbackDataDirectory, "maple_chat_int", EngineMode.LstmOnly);
                experimentalFallbackEngine.SetVariable("user_defined_dpi", "240");
                (fallback, fallbackConfidence) = ReadFontModel(experimentalFallbackEngine, pix);
                chatPasses++;
            }
            chosen = ChatReadingGuard.ChooseWithFallback(regular, candidate, confidence, fallback ?? "", fallbackConfidence, settings);
        }
        else chosen = confidence >= .35 ? ChatReadingGuard.Choose(regular, candidate, settings) : regular;
        if (diagnostics != null)
        {
            Directory.CreateDirectory(diagnostics);
            prepared.Save(Path.Combine(diagnostics, "chat-maple-ocr.png"));
            File.WriteAllText(Path.Combine(diagnostics, "chat-maple.json"), System.Text.Json.JsonSerializer.Serialize(
                new { Current=regular, Candidate=candidate, Chosen=chosen, Confidence=confidence,
                    Fallback=fallback, FallbackConfidence=fallbackConfidence, PrimaryPolicy=hasFallback }));
        }
        return string.IsNullOrWhiteSpace(chosen) ? current : [chosen];
    }

    private static (string Text, float Confidence) ReadFontModel(TesseractEngine model, Pix pix)
    {
        using var page = model.Process(pix, PageSegMode.SingleLine);
        return (page.GetText().Trim(), page.GetMeanConfidence());
    }

    // Preview profile validated on the compact 1920x1080 layout. Other heights use the regular reader.
    internal static Bitmap? PrepareExperimentalChat(Bitmap crop)
    {
        if (crop.Width < 200 || crop.Height is < 34 or > 40) return null;
        using var line = crop.Clone(new Rectangle(10,6,crop.Width-10,25),PixelFormat.Format24bppRgb);
        var background = line.GetPixel(line.Width-2,1);
        if (Math.Max(background.R,Math.Max(background.G,background.B))-Math.Min(background.R,Math.Min(background.G,background.B)) > 25) return null;
        for (var y=0;y<line.Height;y++)
        for (var x=0;x<line.Width;x++)
        {
            var p=line.GetPixel(x,y);
            var contrast=Math.Max(Math.Abs(p.R-background.R),Math.Max(Math.Abs(p.G-background.G),Math.Abs(p.B-background.B)));
            line.SetPixel(x,y,contrast>=30?Color.Black:Color.White);
        }
        var prepared=new Bitmap(line.Width*3+24,line.Height*3+24,PixelFormat.Format24bppRgb);
        using var graphics=Graphics.FromImage(prepared);
        graphics.Clear(Color.White);
        graphics.InterpolationMode=InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode=PixelOffsetMode.Half;
        graphics.DrawImage(line,new Rectangle(12,12,line.Width*3,line.Height*3));
        return prepared;
    }
}
