using System.Text.RegularExpressions;

namespace MapleTracker.Core;

// Retains ordered overlap, rather than globally suppressing repeated message text.
public sealed class LineTracker
{
    private string[] previous = [];
    private int emptyFrames;
    public void Reset() { previous = []; emptyFrames = 0; }
    public IReadOnlyList<string> Update(IEnumerable<string> input)
    {
        var lines = input.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToArray();
        if (lines.Length == 0)
        {
            if (++emptyFrames >= 2) previous = [];
            return [];
        }
        emptyFrames = 0;
        int overlap = 0;
        for (var n = Math.Min(previous.Length, lines.Length); n > 0; n--)
        {
            if (Enumerable.Range(0, n).All(i => Similar(previous[previous.Length - n + i], lines[i]))) { overlap = n; break; }
        }
        previous = lines;
        return lines.Skip(overlap).ToArray();
    }
    private static bool Similar(string a, string b)
    {
        static string Key(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^\p{L}\p{N}>]", "");
        a = Key(a); b = Key(b);
        if (a == b) return true;
        // Never fuzzy-match changed numbers: two different pickup quantities are distinct events.
        if (string.Concat(a.Where(char.IsDigit)) != string.Concat(b.Where(char.IsDigit))) return false;
        var limit = Math.Min(2, Math.Min(a.Length, b.Length) / 12);
        if (limit == 0 || Math.Abs(a.Length - b.Length) > limit) return false;
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = row[0]; row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var old = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = old;
            }
        }
        return row[b.Length] <= limit;
    }
}
