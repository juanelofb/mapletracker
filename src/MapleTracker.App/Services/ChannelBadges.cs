using System.Drawing;

namespace MapleTracker.App.Services;

internal static class ChannelBadges
{
    // Find the connected cream rectangle, rather than OCR-ing the red CH label or ear.
    internal static Rectangle[] Find(Bitmap image, Action<string>? trace = null)
    {
        int width = image.Width, height = image.Height;
        var mask = new bool[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            var p = image.GetPixel(x, y);
            mask[y * width + x] = p.R > 185 && p.G > 170 && p.B > 115 &&
                Math.Max(p.R, Math.Max(p.G, p.B)) - Math.Min(p.R, Math.Min(p.G, p.B)) < 105;
        }
        var result = new List<Rectangle>();
        var queue = new Queue<int>();
        for (int start = 0; start < mask.Length; start++)
        {
            if (!mask[start]) continue;
            mask[start] = false; queue.Enqueue(start);
            int left = start % width, right = left, top = start / width, bottom = top, count = 0;
            while (queue.Count > 0)
            {
                int point = queue.Dequeue(), x = point % width, y = point / width; count++;
                left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                void Visit(int nx, int ny)
                {
                    if (nx < 0 || nx >= width || ny < 0 || ny >= height) return;
                    int index = ny * width + nx;
                    if (mask[index]) { mask[index] = false; queue.Enqueue(index); }
                }
                Visit(x - 1, y); Visit(x + 1, y); Visit(x, y - 1); Visit(x, y + 1);
            }
            int w = right - left + 1, h = bottom - top + 1;
            if (w > 15 && h > 5) trace?.Invoke($"Component {left},{top} {w}x{h} density {count / (double)(w*h):F2}");
            if (w is >= 20 and <= 100 && h is >= 8 and <= 35 && w / (double)h is >= 1.6 and <= 6 &&
                count / (double)(w * h) > .28)
                result.Add(Rectangle.FromLTRB(Math.Max(0, left - 1), Math.Max(0, top - 1), Math.Min(width, right + 2), Math.Min(height, bottom + 2)));
        }
        var rows = new List<Rectangle>();
        foreach (var rect in result.OrderBy(rect => rect.Top).ThenBy(rect => rect.Left))
        {
            int sameRow = rows.FindIndex(other => Math.Min(other.Bottom, rect.Bottom) - Math.Max(other.Top, rect.Top) > Math.Min(other.Height, rect.Height) / 2);
            if (sameRow < 0) rows.Add(rect);
            else if (rect.Left < rows[sameRow].Left) rows[sameRow] = rect;
        }
        return rows.OrderBy(rect => rect.Top).ToArray();
    }
}
