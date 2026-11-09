using System.Text.RegularExpressions;

namespace MapleTracker.Core;

// The compact game bar alternates between received chat and the player's draft.
public sealed class ChatFeedTracker
{
    private readonly LineTracker publishedLines = new();
    private static readonly HashSet<string> InputChannels = new(StringComparer.OrdinalIgnoreCase)
    {
        "all", "al", "ail", "party", "guild", "alliance", "buddy", "whisper", "general", "chat", "say"
    };
    public int IgnoredLines { get; private set; }

    public static bool IsPublishedMessage(string text)
    {
        var message = TradeMessage.Parse(text);
        if (message.Player == "A player" || string.IsNullOrWhiteSpace(message.Body)) return false;
        // Accept normal player names, optional guild tags, and a small OCR-inserted space.
        if (!Regex.IsMatch(message.Player, @"^(?:\[[\p{L}\p{N}_ ]{1,24}\]\s*)?[\p{L}\p{N}_?]{1,24}(?:\s[\p{L}\p{N}_?]{1,8})?$",
            RegexOptions.None, TimeSpan.FromMilliseconds(100))) return false;
        var name = Regex.Replace(message.Player, @"\s", "");
        return !InputChannels.Contains(name);
    }

    public IReadOnlyList<string> Update(IEnumerable<string> observed)
    {
        var raw = observed.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        var valid = raw.Where(IsPublishedMessage).ToArray();
        IgnoredLines = raw.Length - valid.Length;
        // Drafts and OCR blanks must not clear the previously received message.
        return valid.Length == 0 ? [] : publishedLines.Update(valid);
    }

    public void Reset() { publishedLines.Reset(); IgnoredLines = 0; }
}
