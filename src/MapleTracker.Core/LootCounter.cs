using System.Text.RegularExpressions;

namespace MapleTracker.Core;

public sealed class LootCounter
{
    private sealed record Bucket(string RawName, string? Category, int? MapId)
    {
        public long Quantity;
    }
    private GameCatalog catalog;
    private readonly List<Bucket> buckets = [];
    private readonly Dictionary<int, HashSet<int>> known = [];
    private readonly Dictionary<(int Map, int Item), int> evidence = [];
    public Dictionary<string, long> Totals { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Revision { get; private set; }

    public LootCounter(GameCatalog catalog, IReadOnlyDictionary<int, int[]>? learned = null)
    {
        this.catalog = catalog;
        if (learned != null)
            foreach (var (map, items) in learned)
                if (catalog.ContainsMap(map)) known[map] = items.Where(catalog.ContainsItem).ToHashSet();
    }
    public Dictionary<int, int[]> LearnedItems => known.ToDictionary(k => k.Key, v => v.Value.Order().ToArray());

    public void UpdateCatalog(GameCatalog updated)
    {
        catalog = updated;
        foreach (var map in known.Keys.ToArray())
        {
            if (!catalog.ContainsMap(map))
            {
                known.Remove(map);
                continue;
            }
            known[map].RemoveWhere(item => !catalog.ContainsItem(item));
        }
        evidence.Clear();
        Rebuild();
    }

    public ItemReading Add(LootPickup pickup, string originalText, int? mapId)
    {
        if (mapId is int selectedMap && !catalog.ContainsMap(selectedMap)) mapId = null;
        var categoryMatch = Regex.Match(originalText, @"\((Etc|Use|Equip|Equipment|Setup)\)", RegexOptions.IgnoreCase);
        string? category = categoryMatch.Success ? categoryMatch.Groups[1].Value.ToLowerInvariant() switch
        { "use" => "Consumable", "equip" or "equipment" => "Equipment", "etc" => "Etc", _ => "Setup" } : null;
        known.TryGetValue(mapId ?? -1, out var context);
        var resolution = catalog.MatchItem(pickup.Name, category, context);
        var bucket = buckets.FirstOrDefault(b => b.RawName.Equals(pickup.Name, StringComparison.OrdinalIgnoreCase) && b.Category == category && b.MapId == mapId);
        if (bucket == null) { bucket = new(pickup.Name, category, mapId); buckets.Add(bucket); }
        bucket.Quantity += pickup.Quantity;
        bool learned = false;
        // A name seen on a map is a hint, never a claim about a monster's drop table.
        if (mapId is int map && resolution.Id is int item && resolution.Confidence >= .94)
        {
            var key = (map, item); int count = evidence.GetValueOrDefault(key) + 1; evidence[key] = count;
            if (count >= 2)
            {
                if (!known.TryGetValue(map, out var items)) { items = []; known[map] = items; }
                learned = items.Add(item);
            }
        }
        if (learned) Rebuild();
        else { Totals[resolution.Name] = Totals.GetValueOrDefault(resolution.Name) + pickup.Quantity; Revision++; }
        return resolution;
    }
    private void Rebuild()
    {
        Totals.Clear();
        foreach (var bucket in buckets)
        {
            known.TryGetValue(bucket.MapId ?? -1, out var context);
            var resolved = catalog.MatchItem(bucket.RawName, bucket.Category, context);
            Totals[resolved.Name] = Totals.GetValueOrDefault(resolved.Name) + bucket.Quantity;
        }
        Revision++;
    }
    public void Reset() { buckets.Clear(); Totals.Clear(); evidence.Clear(); Revision++; }
}
