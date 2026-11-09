using System.Globalization;
using System.Text.RegularExpressions;

namespace MapleTracker.Core;

public sealed record HudCandidate(string Text, float Confidence);

public static class HudConsensus
{
    // A good number read and a good percent read need not come from the same OCR pass.
    public static HudReading? Read(string levelText, IReadOnlyList<HudCandidate> candidates, ExperienceTable table)
    {
        if (!int.TryParse(levelText.Trim(), out var level) || level is < 1 or > 99 || table.Required(level) is not long required) return null;
        var numbers = new List<(long Value, float Confidence)>();
        var percentages = new List<double>();
        foreach (var candidate in candidates)
        {
            var number = Regex.Match(candidate.Text, @"^\s*(?:[A-Za-z]{1,3}\s*[:.]?\s*)?(?<number>\d{1,3}(?:[.,]\d{3})+|\d+)(?=\s|[\[(]|$)", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            if (number.Success && TextParsing.Number(number.Groups["number"].Value.Replace(".", "")) is long value && value < required)
                numbers.Add((value, candidate.Confidence));
            var percent = Regex.Match(candidate.Text, @"(?<![\d.])(?<percent>\d{1,2}\.\d{2})\s*%", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            if (percent.Success && double.TryParse(percent.Groups["percent"].Value, CultureInfo.InvariantCulture, out var p)) percentages.Add(p);
        }
        var eligible = numbers.Where(n => percentages.Any(p => Math.Abs(100.0 * n.Value / required - p) <= .03))
            .GroupBy(n => n.Value).Select(g => new { Value = g.Key, Confidence = g.Max(n => n.Confidence), Votes = g.Count() })
            .OrderByDescending(n => n.Votes).ThenByDescending(n => n.Confidence).ToArray();
        if (eligible.Length == 0) return null;
        // Widely separated possibilities mean the evidence is contradictory.
        if (eligible.Skip(1).Any(n => Math.Abs(n.Value - eligible[0].Value) > required * .0006)) return null;
        return new(level, eligible[0].Value);
    }
}
