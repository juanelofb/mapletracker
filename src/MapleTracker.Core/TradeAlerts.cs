using System.Text;
using System.Text.RegularExpressions;

namespace MapleTracker.Core;

public sealed record TradeMarker(int Index, int Length, MessageKind Kind);
public sealed record TradeMessage(string Player, string Body)
{
    public static TradeMessage Parse(string text)
    {
        int brackets = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '[') brackets++;
            else if (text[i] == ']') brackets = Math.Max(0, brackets - 1);
            else if (text[i] == ':' && brackets == 0)
            {
                var player = text[..i].Trim();
                return new(player.Length > 0 ? player : "A player", text[(i + 1)..].Trim());
            }
        }
        return new("A player", text.Trim());
    }

    public static IReadOnlyList<TradeMarker> Markers(string body, TrackerSettings settings)
    {
        var markers = new List<TradeMarker>();
        foreach (var (terms, kind) in new[] { (settings.BuyingTerms, MessageKind.Buying), (settings.SellingTerms, MessageKind.Selling) })
        foreach (var term in terms.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            var literal = term.Trim();
            var compact = Regex.Replace(literal, @"\s", "");
            var pattern = compact.Length == 2 && "BSbs".Contains(compact[0]) && "><".Contains(compact[1])
                ? Regex.Escape(compact[..1]) + @"\s*" + Regex.Escape(compact[1..]) : Regex.Escape(literal);
            pattern = @"(?<!\w)" + pattern + (char.IsLetterOrDigit(literal[^1]) ? @"(?!\w)" : "");
            foreach (Match match in Regex.Matches(body, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
            {
                // Linked item names may themselves contain words that resemble trade terms.
                if (!literal.StartsWith('[') && body.LastIndexOf('[', match.Index) > body.LastIndexOf(']', match.Index)) continue;
                markers.Add(new(match.Index, match.Length, kind));
            }
        }
        return markers.Distinct().OrderBy(m => m.Index).ThenByDescending(m => m.Length).ToArray();
    }
}

public sealed record TradeAlert(DateTimeOffset Time, string Player, string Item, MessageKind Intent, string Message)
{
    public string Summary => Intent == MessageKind.Buying ? $"{Player} wants to buy {Item}" : $"{Player} is selling {Item}";
    public string ListLabel => Intent == MessageKind.Buying ? "MATCH FOR YOUR SELLING LIST" : "MATCH FOR YOUR BUYING LIST";
}

public sealed class TradeAlertMatcher(TrackerSettings settings)
{
    private readonly Dictionary<string, DateTimeOffset> notified = new(StringComparer.Ordinal);
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);
    public void Reset() => notified.Clear();

    public IReadOnlyList<TradeAlert> Match(string text, DateTimeOffset now)
    {
        var message = TradeMessage.Parse(text);
        var results = new List<TradeAlert>();
        foreach (var (items, intent) in new[] { (settings.SellingItems, MessageKind.Buying), (settings.BuyingItems, MessageKind.Selling) })
        foreach (var entry in items.Where(i => !string.IsNullOrWhiteSpace(i)))
        {
            var aliases = entry.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (aliases.Length == 0) continue;
            if (!aliases.Any(alias => MatchesIntent(message.Body, alias, intent))) continue;
            var key = $"{Normalize(message.Player)}\0{Normalize(aliases[0])}\0{intent}";
            if (notified.TryGetValue(key, out var last) && now - last < Cooldown) continue;
            notified[key] = now;
            results.Add(new(now, message.Player, aliases[0], intent, text));
        }
        if (notified.Count > 1000)
            foreach (var key in notified.Where(p => now - p.Value >= Cooldown).Select(p => p.Key).ToArray()) notified.Remove(key);
        return results;
    }

    private bool MatchesIntent(string body, string item, MessageKind intent)
    {
        var words = Regex.Matches(item, @"[\p{L}\p{N}]+", RegexOptions.None, TimeSpan.FromMilliseconds(100)).Select(m => Regex.Escape(m.Value)).ToArray();
        if (words.Length == 0) return false;
        var pattern = @"(?<![\p{L}\p{N}])" + string.Join(@"[^\p{L}\p{N}]+", words) + @"(?![\p{L}\p{N}])";
        foreach (var clause in Regex.Split(body, @"\s*[/;|\r\n]+\s*"))
        {
            var markers = TradeMessage.Markers(clause, settings);
            if (markers.Count == 0) continue;
            foreach (Match match in Regex.Matches(clause, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
            {
                var previous = markers.LastOrDefault(m => m.Index <= match.Index);
                var next = markers.FirstOrDefault(m => m.Index >= match.Index + match.Length);
                // Item links commonly precede the marker: [Steel Ore] S> 100 each.
                bool linkedBeforeNext = next != null && Regex.IsMatch(clause[(match.Index + match.Length)..next.Index], @"^\s*\]\s*$");
                var assigned = linkedBeforeNext ? next : previous ?? next;
                if (assigned?.Kind == intent) return true;
            }
        }
        return false;
    }
    private static string Normalize(string value) => string.Concat(value.Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit)).ToLowerInvariant();
}
