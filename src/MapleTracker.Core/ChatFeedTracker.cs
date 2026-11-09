using System.Text.RegularExpressions;

namespace MapleTracker.Core;

// The compact game bar alternates between received chat and the player's draft.
public sealed class ChatFeedTracker
{
    private sealed class VisibleMessage(string text)
    {
        public readonly string Anchor = text;
        public readonly Dictionary<string, int> NumberSightings = [];
        public bool Published;
    }
    private VisibleMessage[] visible = [];
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

    public IReadOnlyList<string> Update(IEnumerable<string> observed, bool historyOnly = false)
    {
        var raw = observed.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        // A separately selected history region never contains the draft input.
        // Recognizable system notices need no sender colon; fragments still need validation.
        var valid = raw.Where(line => historyOnly ? IsHistoryMessage(line) : IsPublishedMessage(line)).ToArray();
        IgnoredLines = raw.Length - valid.Length;
        // Drafts and OCR blanks must not clear the previously received message.
        return valid.Length == 0 ? [] : UpdateVisible(valid, historyOnly ? 2 : 1);
    }

    public static bool IsHistoryMessage(string text) => IsPublishedMessage(text) ||
        Regex.IsMatch(text, @"^[\p{L}\p{N}_]{1,24}\s+(?:has|have)\s+(?:left|joined|entered)\s+(?:the|your|a)\s+(?:party|guild|alliance)\b", RegexOptions.IgnoreCase);

    private IReadOnlyList<string> UpdateVisible(string[] lines, int confirmations)
    {
        int overlap = 0;
        for (int n = Math.Min(visible.Length, lines.Length); n > 0; n--)
            if (Enumerable.Range(0, n).All(i => SameVisibleMessage(visible[visible.Length - n + i].Anchor, lines[i]))) { overlap = n; break; }
        var next = visible.Skip(visible.Length - overlap).Concat(lines.Skip(overlap).Select(line => new VisibleMessage(line))).ToArray();
        var added = new List<string>();
        for (int i = 0; i < next.Length; i++)
        {
            var item = next[i];
            if (item.Published) continue;
            var numbers = string.Concat(TradeMessage.Parse(lines[i]).Body.Where(char.IsDigit));
            int sightings = item.NumberSightings.GetValueOrDefault(numbers) + 1;
            item.NumberSightings[numbers] = sightings;
            if (item.NumberSightings.Count > 32) item.NumberSightings.Remove(item.NumberSightings.Keys.First());
            if (sightings >= confirmations) { item.Published = true; added.Add(lines[i]); }
        }
        // Preserve the first confirmed anchor, rather than letting OCR drift create new messages.
        visible = next;
        return added;
    }

    public static bool SameVisibleMessage(string a, string b)
    {
        static string Key(string text) => string.Concat(text.ToLowerInvariant().Where(char.IsLetterOrDigit));
        var x = TradeMessage.Parse(a); var y = TradeMessage.Parse(b);
        var senderA = Key(x.Player); var senderB = Key(y.Player);
        if (!Close(senderA, senderB, Math.Max(1, Math.Min(senderA.Length, senderB.Length) / 5))) return false;
        var bodyA = Key(x.Body); var bodyB = Key(y.Body);
        return Close(bodyA, bodyB, Math.Min(bodyA.Length, bodyB.Length) < 12 ? 1 : Math.Max(2, (int)(Math.Min(bodyA.Length, bodyB.Length) * .22)));
    }

    private static bool Close(string a, string b, int limit)
    {
        if (a == b) return true;
        if (Math.Abs(a.Length - b.Length) > limit) return false;
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int diagonal = row[0]; row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int old = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1)); diagonal = old;
            }
        }
        return row[b.Length] <= limit;
    }

    public void Reset() { visible = []; IgnoredLines = 0; }
}
