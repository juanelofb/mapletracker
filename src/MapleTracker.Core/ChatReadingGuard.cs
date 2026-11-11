using System.Text.RegularExpressions;

namespace MapleTracker.Core;

public static class ChatReadingGuard
{
    public static bool CanUsePrimary(string candidate, float confidence) =>
        float.IsFinite(confidence) && confidence is >= .9f and <= 1f && IsCandidateValid(candidate);

    public static string ChooseWithFallback(string current, string primary, float primaryConfidence,
        string fallback, float fallbackConfidence, TrackerSettings settings)
    {
        if (CanUsePrimary(primary, primaryConfidence)) return primary;
        return float.IsFinite(fallbackConfidence) && fallbackConfidence is >= .35f and <= 1f
            ? Choose(current, fallback, settings) : current;
    }

    private static bool IsCandidateValid(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Contains('\n') || candidate.Contains('\r')) return false;
        var message = TradeMessage.Parse(candidate);
        return Regex.IsMatch(message.Player, @"^[\p{L}\p{N}_]{1,24}$") && message.Body.Any(char.IsLetterOrDigit);
    }

    public static string Choose(string current, string candidate, TrackerSettings settings)
    {
        if (string.IsNullOrWhiteSpace(current) || !IsCandidateValid(candidate)) return current;
        var player = TradeMessage.Parse(candidate).Player;
        if (!Regex.IsMatch(player, @"^[\p{L}\p{N}_]{1,24}$")) return current;
        // Choose one complete observed reading; never splice or invent a corrected price.
        if (!Digits(current).SequenceEqual(Digits(candidate)) ||
            !Digits(TradeMessage.Parse(current).Player).SequenceEqual(Digits(player)) ||
            !NumericTokens(current).SequenceEqual(NumericTokens(candidate)) || !Prices(current).SequenceEqual(Prices(candidate)) ||
            !Tags(current).SequenceEqual(Tags(candidate))) return current;
        var classifier = new TradeClassifier(settings);
        if (classifier.Classify(current) != classifier.Classify(candidate)) return current;
        return candidate;
    }

    private static string[] Digits(string text) => Regex.Matches(text, @"\d+").Select(m => m.Value).ToArray();
    private static string[] NumericTokens(string text) => Regex.Matches(TradeMessage.Parse(text).Body,
        @"(?<!\d)[+\-]?\d+(?:[.,]\d+)?(?:\s*[%km])?", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))
        .Select(m => Regex.Replace(m.Value, @"\s", "").ToLowerInvariant()).ToArray();
    private static string[] Prices(string text) => Regex.Matches(TradeMessage.Parse(text).Body,
        @"(\d+(?:[.,]\d+)?)\s*([km])", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))
        .Select(m => m.Groups[1].Value.Replace(',', '.') + m.Groups[2].Value.ToLowerInvariant()).ToArray();
    private static string[] Tags(string text) => Regex.Matches(TradeMessage.Parse(text).Body,
        @"(?<!\w)([bs])\s*([><])", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))
        .Select(m => m.Groups[1].Value.ToLowerInvariant() + m.Groups[2].Value).ToArray();
}
