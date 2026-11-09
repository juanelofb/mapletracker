using System.Text.RegularExpressions;

namespace MapleTracker.Core;

public sealed record NotificationLine(string Text, double Top, double Height, float Confidence, double Brightness = 160);

// Fading OCR variants belong to a notification's lifetime, not separate pickup events.
public sealed class LootFeedTracker
{
    private sealed class Track(NotificationLine line, TimeSpan time)
    {
        public NotificationLine Best = line;
        public double Top = line.Top;
        public double Brightness = line.Brightness;
        public TimeSpan LastSeen = time;
        public readonly Dictionary<long, (int Count, NotificationLine Best)> Evidence = [];
        public bool Counted;
    }
    private readonly List<Track> tracks = [];
    private static readonly TimeSpan Retention = TimeSpan.FromSeconds(4);
    public void Reset() => tracks.Clear();

    public IReadOnlyList<string> Update(IEnumerable<NotificationLine> observed, TimeSpan now)
    {
        tracks.RemoveAll(t => now - t.LastSeen > Retention);
        var previous = tracks.OrderBy(t => t.Top).ToArray();
        var current = observed.Where(line => Signature(line.Text) != null).OrderBy(line => line.Top).ToArray();
        var used = new HashSet<Track>();
        var assignments = new Dictionary<int, Track>();

        // Ordered alignment permits missing/fading rows and upward movement when new rows arrive.
        var score = new double[previous.Length + 1, current.Length + 1];
        for (int i = 1; i <= previous.Length; i++)
        for (int j = 1; j <= current.Length; j++)
        {
            var similarity = Similarity(previous[i - 1].Best.Text, current[j - 1].Text);
            var movement = current[j - 1].Top - previous[i - 1].Top;
            var match = similarity >= .68 && movement <= .12 && !Renewed(previous[i - 1], current[j - 1], now) ? 10 + similarity - Math.Abs(movement) : -1000;
            score[i, j] = Math.Max(Math.Max(score[i - 1, j], score[i, j - 1]), score[i - 1, j - 1] + match);
        }
        int a = previous.Length, b = current.Length;
        while (a > 0 && b > 0)
        {
            var similarity = Similarity(previous[a - 1].Best.Text, current[b - 1].Text);
            var movement = current[b - 1].Top - previous[a - 1].Top;
            var match = similarity >= .68 && movement <= .12 && !Renewed(previous[a - 1], current[b - 1], now) ? 10 + similarity - Math.Abs(movement) : -1000;
            if (Math.Abs(score[a, b] - score[a - 1, b - 1] - match) < .0001)
            {
                assignments[b - 1] = previous[a - 1]; used.Add(previous[a - 1]); a--; b--;
            }
            else if (score[a - 1, b] >= score[a, b - 1]) a--;
            else b--;
        }

        var added = new List<string>();
        for (int i = 0; i < current.Length; i++)
        {
            var line = current[i];
            if (!assignments.TryGetValue(i, out var track))
            {
                // Severe OCR degradation at the same row is not proof of a new event.
                track = previous.Where(t => !used.Contains(t) && SameType(t.Best.Text, line.Text) &&
                    Math.Abs(t.Top - line.Top) < Math.Max(.04, line.Height * .5) && !Renewed(t, line, now) && line.Brightness < t.Brightness + 35)
                    .OrderBy(t => Math.Abs(t.Top - line.Top)).FirstOrDefault();
                if (track == null) { track = new(line, now); tracks.Add(track); }
                used.Add(track);
            }
            track.Top = line.Top;
            track.LastSeen = now;
            track.Brightness = Math.Min(track.Brightness, line.Brightness);
            if (line.Confidence > track.Best.Confidence) track.Best = line;
            var amount = Signature(line.Text)!.Value.Amount;
            if (line.Confidence >= 55)
            {
                var evidence = track.Evidence.GetValueOrDefault(amount, (Count: 0, Best: line));
                track.Evidence[amount] = (evidence.Count + 1, line.Confidence > evidence.Best.Confidence ? line : evidence.Best);
            }
            // Two readable frames must agree on the amount, not just on "some loot".
            if (!track.Counted && track.Evidence.TryGetValue(amount, out var confirmed) && confirmed.Count >= 2)
            {
                track.Counted = true;
                track.Best = confirmed.Best;
                added.Add(confirmed.Best.Text);
            }
        }
        return added;
    }

    private static bool Renewed(Track track, NotificationLine line, TimeSpan now) =>
        now - track.LastSeen > TimeSpan.FromSeconds(1.5) && line.Brightness >= 145 ||
        track.Brightness < 110 && line.Brightness >= track.Brightness + 45 && Math.Abs(track.Top - line.Top) < .04;

    private static (string Kind, string Name, long Amount)? Signature(string text)
    {
        if (TextParsing.Loot(text) is { } item) return ("item", Normalize(item.Name), item.Quantity);
        if (TextParsing.Mesos(text) is long mesos) return ("meso", "meso", mesos);
        var exp = Regex.Match(text, @"\bEXP\s*\(\s*\+\s*(\d+)\s*\)", RegexOptions.IgnoreCase);
        return exp.Success && long.TryParse(exp.Groups[1].Value, out var amount) ? ("exp", "exp", amount) : null;
    }
    private static bool SameType(string a, string b)
    {
        var x = Signature(a); var y = Signature(b);
        return x != null && y != null && x.Value.Kind == y.Value.Kind;
    }
    private static string Normalize(string name) => Regex.Replace(name.ToLowerInvariant(), @"[^\p{L}\p{N}]", "");
    private static double Similarity(string a, string b)
    {
        var x = Signature(a); var y = Signature(b);
        if (x == null || y == null || x.Value.Kind != y.Value.Kind || x.Value.Amount != y.Value.Amount) return 0;
        var s = x.Value.Name; var t = y.Value.Name;
        if (s == t) return 1;
        var row = Enumerable.Range(0, t.Length + 1).ToArray();
        for (int i = 1; i <= s.Length; i++)
        {
            var diagonal = row[0]; row[0] = i;
            for (int j = 1; j <= t.Length; j++)
            {
                int old = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (s[i - 1] == t[j - 1] ? 0 : 1));
                diagonal = old;
            }
        }
        return 1.0 - (double)row[t.Length] / Math.Max(1, Math.Max(s.Length, t.Length));
    }
}
