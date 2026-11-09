using System.Drawing;
using System.Text.RegularExpressions;
using Tesseract;

namespace MapleTracker.App.Services;

public sealed partial class OcrReader
{
    private int chatPasses;
    internal string[] ReadChat(Bitmap frame, TrackerSettings settings, string? diagnostics)
    {
        using var crop = WindowCapture.Crop(frame, settings.Chat);
        var badges = settings.IgnoreChannelBadges ? ChannelBadges.Find(crop) : [];
        if (badges.Length < 2)
            return ReadChatRow(crop, badges.FirstOrDefault(), badges.Length > 0, settings, diagnostics, "chat");

        var lines = new List<string>();
        int previousBottom = 0;
        for (int i = 0; i < badges.Length; i++)
        {
            var badge = badges[i];
            int top = Math.Max(previousBottom, badge.Top - 3), bottom = Math.Min(crop.Height, badge.Bottom + 3);
            if (i + 1 < badges.Length) bottom = Math.Min(bottom, (badge.Bottom + badges[i + 1].Top) / 2);
            if (top - previousBottom >= 10)
            {
                using var gap = crop.Clone(new Rectangle(0, previousBottom, crop.Width, top - previousBottom), crop.PixelFormat);
                lines.AddRange(ReadChatRow(gap, Rectangle.Empty, false, settings, diagnostics, $"chat-gap-{i}"));
            }
            using var row = crop.Clone(new Rectangle(0, top, crop.Width, bottom - top), crop.PixelFormat);
            var relativeBadge = badge; relativeBadge.Offset(0, -top);
            lines.AddRange(ReadChatRow(row, relativeBadge, true, settings, diagnostics, $"chat-row-{i}"));
            previousBottom = bottom;
        }
        if (crop.Height - previousBottom >= 10)
        {
            using var gap = crop.Clone(new Rectangle(0, previousBottom, crop.Width, crop.Height - previousBottom), crop.PixelFormat);
            lines.AddRange(ReadChatRow(gap, Rectangle.Empty, false, settings, diagnostics, "chat-tail"));
        }
        return lines.ToArray();
    }

    private string[] ReadChatRow(Bitmap crop, Rectangle badge, bool hasBadge, TrackerSettings settings, string? diagnostics, string name)
    {
        if (hasBadge && badge.Left >= 8 && crop.Width - badge.Right >= 8)
        {
            // The icon is omitted completely. Sender and body cannot borrow characters from it.
            using var sender = crop.Clone(new Rectangle(0, 0, badge.Left, crop.Height), crop.PixelFormat);
            using var body = crop.Clone(new Rectangle(badge.Right, 0, crop.Width - badge.Right, crop.Height), crop.PixelFormat);
            var player = string.Join(" ", RecognizeChatPart(sender, settings, diagnostics, name + "-sender")).Trim(' ', '[', ']', '|', ':', ';', '.', '-');
            var text = string.Join(" ", RecognizeChatPart(body, settings, diagnostics, name + "-body")).TrimStart(' ', ':', ';', '.', '|', '-', '_', '\'', '‘', '’');
            if (Regex.IsMatch(player, @"^[\p{L}\p{N}_?]{1,24}(?:\s[\p{L}\p{N}_?]{1,8})?$") && text.Any(char.IsLetterOrDigit))
                return [$"{player}: {text}"];
            return [];
        }
        return RecognizeChatPart(crop, settings, diagnostics, name);
    }

    private string[] RecognizeChatPart(Bitmap crop, TrackerSettings settings, string? diagnostics, string name)
    {
        var mode = crop.Height <= 40 ? PageSegMode.SingleLine : PageSegMode.SingleBlock;
        int passes = settings.RecognitionQuality switch { RecognitionQuality.Low => 1, RecognitionQuality.High => 4, _ => 2 };
        bool banner = BannerBackground(crop) != null;
        var candidates = new List<NotificationLine[]>();
        for (int i = 0; i < passes; i++)
        {
            // Try distinct views; all use one shared OCR engine and sequential image buffers.
            bool gray = i == 1;
            int threshold = i == 2 ? 150 : i == 3 ? 0 : banner || gray ? 0 : 100;
            var lines = ReadRegion(crop, new CaptureRegion(0, 0, 1, 1), name + "-pass" + i, i == 2 || !banner, diagnostics,
                mode == PageSegMode.SingleLine, threshold, mode, gray, banner && i == 0, settings.IgnoreChannelBadges);
            chatPasses++;
            candidates.Add(lines.Where(line => line.Confidence >= 35 && line.Text.Count(char.IsLetterOrDigit) >= 2).ToArray());
        }
        var choices = candidates.Where(c => c.Length > 0).ToArray();
        if (choices.Length == 0) return [];
        int longest = choices.Max(c => c.Sum(l => l.Text.Count(char.IsLetterOrDigit)));
        choices = choices.Where(c => c.Sum(l => l.Text.Count(char.IsLetterOrDigit)) >= longest * .6).ToArray();
        static string Numbers(NotificationLine[] lines) => string.Concat(string.Join(" ", lines.Select(l => l.Text)).Where(char.IsDigit));
        // Prefer repeated numeric evidence over a single confident wrong price. Never invent digits.
        var best = choices.MaxBy(c => c.Average(l => l.Confidence) + choices.Count(other => Numbers(other) == Numbers(c)) * 8 + Math.Min(40, c.Sum(l => l.Text.Length) * .6) +
            (c.Any(l => ChatFeedTracker.IsPublishedMessage(l.Text)) ? 12 : 0));
        return best!.Select(l => l.Text).ToArray();
    }
}
