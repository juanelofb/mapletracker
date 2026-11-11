using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace MapleTracker.App.Services;

// High-pass shapes of "mesos (+", digits and ')', independent of map scenery.
internal sealed class MesoGlyphReader(float[] prefix, float[][] digits, float[] close)
{
    internal sealed record Row(double Top, NotificationLine? Line);
    private sealed record Candidate(int X, int Y, int Count, float Score, float Strength);
    private const int Height = 18, PrefixWidth = 76, DigitWidth = 10, CloseWidth = 8;

    public static MesoGlyphReader Load(byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        float[] Unit(JsonElement e, int count)
        {
            var values = e.EnumerateArray().Select(v => (float)v.GetInt32()).ToArray();
            var norm = MathF.Sqrt(values.Sum(v => v * v));
            if (values.Length != count || norm <= 0) throw new InvalidDataException("Invalid bundled meso glyph template.");
            for (int i = 0; i < values.Length; i++) values[i] /= norm;
            return values;
        }
        var digitData = root.GetProperty("digits").EnumerateArray().Select(e => Unit(e, DigitWidth * Height)).ToArray();
        if (digitData.Length != 10) throw new InvalidDataException("Missing bundled meso digits.");
        return new(Unit(root.GetProperty("prefix"), PrefixWidth * Height), digitData, Unit(root.GetProperty("close"), CloseWidth * Height));
    }

    public Row[] Read(Bitmap crop, int digitEnd)
    {
        if (digitEnd < PrefixWidth + DigitWidth || digitEnd + CloseWidth > crop.Width || crop.Height < Height) return [];
        int left = Math.Max(0, digitEnd - 144), width = crop.Width - left;
        var pixels = HighPass(crop, left, width);
        var candidates = new List<Candidate>();
        for (int count = 1; count <= 6; count++)
        {
            int x = digitEnd - left - PrefixWidth - count * DigitWidth;
            if (x < 0) break;
            for (int y = 0; y <= crop.Height - Height; y++)
            {
                var score = Correlate(pixels, width, x, y, PrefixWidth, prefix, out float strength);
                if (score >= .60f && strength >= 3.5f) candidates.Add(new(x, y, count, score, strength));
            }
        }
        var used = new List<int>(); var rows = new List<Row>();
        foreach (var match in candidates.OrderByDescending(c => c.Score))
        {
            if (used.Any(y => Math.Abs(y - match.Y) < 10)) continue;
            used.Add(match.Y);
            double top = (match.Y + 3.0) / crop.Height;
            long amount = 0;
            bool valid = BestOffset(digitEnd - left, match.Y, CloseWidth, close) >= .60f;
            for (int i = 0; valid && i < match.Count; i++)
            {
                int x = match.X + PrefixWidth + i * DigitWidth;
                float best = -1, second = -1; int digit = -1;
                for (int d = 0; d < digits.Length; d++)
                {
                    float score = BestOffset(x, match.Y, DigitWidth, digits[d]);
                    if (score > best) { second = best; best = score; digit = d; }
                    else if (score > second) second = score;
                }
                valid = best >= .72f && best - second >= .10f && (i > 0 || digit != 0);
                if (valid) amount = amount * 10 + digit;
            }
            var line = valid && amount > 0 ? new NotificationLine($"You have gained mesos (+{amount})", top,
                14.0 / crop.Height, 100, Math.Clamp(30 + 8 * match.Strength, 0, 255)) : null;
            rows.Add(new(top, line));
        }
        return rows.OrderBy(r => r.Top).ToArray();

        float BestOffset(int x, int y, int glyphWidth, float[] template)
        {
            float best = -1;
            for (int offset = -1; offset <= 1; offset++)
            {
                int candidate = y + offset;
                if (candidate >= 0 && candidate + Height <= crop.Height)
                    best = Math.Max(best, Correlate(pixels, width, x, candidate, glyphWidth, template, out _));
            }
            return best;
        }
    }

    private static float Correlate(float[] pixels, int stride, int x, int y, int width, float[] template, out float strength)
    {
        float dot = 0, norm = 0; int k = 0;
        for (int row = 0; row < Height; row++)
        {
            int start = (y + row) * stride + x;
            for (int col = 0; col < width; col++)
            {
                float value = pixels[start + col]; dot += value * template[k++]; norm += value * value;
            }
        }
        strength = dot / MathF.Sqrt(template.Length);
        return dot / MathF.Sqrt(Math.Max(norm, 1));
    }

    private static unsafe float[] HighPass(Bitmap crop, int left, int width)
    {
        int height = crop.Height; var luminance = new int[width * height];
        var data = crop.LockBits(new Rectangle(0, 0, crop.Width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var p = (byte*)data.Scan0 + y * data.Stride + (left + x) * 3;
                luminance[y * width + x] = (p[2] * 299 + p[1] * 587 + p[0] * 114 + 500) / 1000;
            }
        }
        finally { crop.UnlockBits(data); }
        var result = new float[luminance.Length];
        for (int y = 1; y < height - 1; y++)
        for (int x = 1; x < width - 1; x++)
        {
            int sum = 0;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++) sum += luminance[(y + dy) * width + x + dx];
            result[y * width + x] = luminance[y * width + x] - (sum + 4) / 9;
        }
        return result;
    }
}
