using System.Globalization;
using System.Text.RegularExpressions;

namespace MapleTracker.Core;

public static class TextParsing
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(150);
    private static Match Match(string text, string pattern) => Regex.Match(text, pattern, RegexOptions.IgnoreCase, Timeout);
    public static long? Number(string text) => long.TryParse(Regex.Replace(text, @"[,\s]", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    public static HudReading? Hud(string levelText, string expText, ExperienceTable table)
    {
        var levelMatch = Match(levelText, @"(?:LV\.?\s*)?(?<level>\d{1,3})(?!\d)");
        var expMatch = Match(expText, @"(?:EXP\s*[:.]?\s*)?(?<exp>\d{1,3}(?:[.,]\d{3})+|\d+)(?:\s*[\[(]?\s*(?<percent>\d{1,3}\.\d+)\s*%\s*[\])]?)?");
        if (!int.TryParse(levelMatch.Groups["level"].Value, out var level) || level is < 1 or > 100 || !expMatch.Success) return null;
        var exp = Number(expMatch.Groups["exp"].Value.Replace(".", ""));
        if (exp == null || table.Required(level) is long max && exp >= max) return null;
        if (expMatch.Groups["percent"].Success && table.Required(level) is long required &&
            double.TryParse(expMatch.Groups["percent"].Value, CultureInfo.InvariantCulture, out var percent) && Math.Abs(100.0 * exp.Value / required - percent) > .3) return null;
        return new(level, exp.Value);
    }

    public static LootPickup? Loot(string text)
    {
        var m = Match(text, @"^(?<name>.+?)\s*[x×]\s*(?<qty>\d[\d,]*)\s+(?:earned|obtained|received)\b");
        if (!m.Success) m = Match(text, @"(?:obtained|received|picked up)(?:\s+an?\s+item)?\s*[:(]\s*(?<name>[^)]+?)\s*[x×]\s*(?<qty>\d[\d,]*)\s*\)?\s*$");
        if (!m.Success) return null;
        var name = m.Groups["name"].Value.Trim();
        var qty = Number(m.Groups["qty"].Value);
        return qty is > 0 and <= 999999 && name.Length > 0 && !Regex.IsMatch(name, @"\b(?:EXP|mesos?)\b", RegexOptions.IgnoreCase, Timeout) ? new(name, qty.Value) : null;
    }

    public static long? Mesos(string text)
    {
        var m = Match(text, @"(?:received|gained|obtained|picked up)\s+(?<amount>\d[\d,]*)\s+mesos?\b");
        if (!m.Success) m = Match(text, @"\bmesos?\s*(?:[(:]\s*\+?|\+)(?<amount>\d[\d,]*)");
        if (!m.Success) m = Match(text, @"(?:received|gained|obtained)\s+mesos?\s*[(:]?\s*\+?(?<amount>\d[\d,]*)");
        return m.Success ? Number(m.Groups["amount"].Value) : null;
    }
}
