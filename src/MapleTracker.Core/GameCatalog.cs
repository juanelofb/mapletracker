using System.Globalization;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace MapleTracker.Core;

public sealed record CatalogItem(int Id, string Name, string Category);
public sealed record CatalogMonster(int Id, string Name);
public sealed record CatalogMap(int Id, string Name, string StreetName, int[] MobIds);
public sealed record MapReading(int? Id, string Name, string StreetName, double Confidence);
public sealed record ItemReading(string Name, int? Id, double Confidence, string Basis);

public static class NameSimilarity
{
    public static string Key(string text) => string.Concat(text.Normalize(NormalizationForm.FormD)
        .Where(c => char.IsLetterOrDigit(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)).ToLowerInvariant();

    public static double Score(string a, string b)
    {
        if (a == b) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;
        Span<int> row = b.Length <= 256 ? stackalloc int[b.Length + 1] : new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) row[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            int diagonal = row[0]; row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int old = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = old;
            }
        }
        return 1.0 - (double)row[b.Length] / Math.Max(a.Length, b.Length);
    }
}

public sealed class GameCatalog
{
    private sealed record ItemName(CatalogItem Item, string Key);
    private sealed record MapName(CatalogMap Map, string Name, string Street);
    private readonly ItemName[] itemNames;
    private readonly Dictionary<string, ItemName[]> exactItems;
    private readonly Dictionary<string, ItemName[]> itemsByCategory;
    private readonly HashSet<int> itemIds;
    private readonly MapName[] mapNames;
    private readonly Dictionary<int, CatalogMap> mapsById;
    private readonly Dictionary<int, CatalogMonster> monstersById;
    private readonly HashSet<string> streetNames;
    private readonly ConcurrentDictionary<string, MapReading?> mapCache = new(StringComparer.Ordinal);
    public IReadOnlyList<CatalogItem> Items { get; }
    public IReadOnlyList<CatalogMap> Maps { get; }
    public string DataUpdatedAt { get; }

    public GameCatalog(IEnumerable<CatalogItem> items, IEnumerable<CatalogMap> maps, IEnumerable<CatalogMonster> monsters, string dataUpdatedAt = "")
    {
        Items = items.ToArray(); Maps = maps.ToArray(); DataUpdatedAt = dataUpdatedAt;
        itemNames = Items.Where(i => !string.IsNullOrWhiteSpace(i.Name)).Select(i => new ItemName(i, NameSimilarity.Key(i.Name))).ToArray();
        exactItems = itemNames.GroupBy(i => i.Key).ToDictionary(g => g.Key, g => g.ToArray());
        itemsByCategory = itemNames.GroupBy(i => i.Item.Category, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        itemIds = Items.Select(i => i.Id).ToHashSet();
        mapNames = Maps.Select(m => new MapName(m, NameSimilarity.Key(m.Name), NameSimilarity.Key(m.StreetName))).ToArray();
        mapsById = Maps.ToDictionary(m => m.Id);
        monstersById = monsters.ToDictionary(m => m.Id);
        streetNames = mapNames.Select(m => m.Street).Where(k => k.Length > 0).ToHashSet();
    }
    public static GameCatalog Load(Stream stream) => CatalogSnapshot.Read(stream).BuildCatalog();
    public bool ContainsItem(int id) => itemIds.Contains(id);
    public bool ContainsMap(int id) => mapsById.ContainsKey(id);
    public string[] MonsterNames(int? mapId) => mapId is int id && mapsById.TryGetValue(id, out var map)
        ? map.MobIds.Where(monstersById.ContainsKey).Select(m => monstersById[m].Name).Distinct().Order().ToArray() : [];

    public MapReading? MatchMap(IEnumerable<string> lines)
    {
        var visible = lines.Select(s => s.Trim()).Where(s => s.Length >= 4 && s.Length <= 160).Take(4).ToArray();
        var cacheKey = string.Join("\n", visible);
        if (mapCache.TryGetValue(cacheKey, out var cached)) return cached;
        var keys = visible.Select(s => Regex.Replace(s, @"\b[il1|]{1,4}$", m => new string('I', m.Length), RegexOptions.IgnoreCase))
            .Select(NameSimilarity.Key).Where(s => s.Length >= 4).ToArray();
        if (keys.Length == 0) return null;

        // The final line is the map name. Compare the street only for plausible matches.
        var ranked = mapNames.Select(m => (Entry: m, Score: NameSimilarity.Score(keys[^1], m.Name)))
            .Where(r => r.Score >= .84)
            .Select(r => (r.Entry, r.Score, Street: keys.Max(k => NameSimilarity.Score(k, r.Entry.Street))))
            .OrderByDescending(r => r.Score).ThenByDescending(r => r.Street).ToArray();
        MapReading? result = null;
        if (ranked.Length > 0)
        {
            var best = ranked[0];
            var others = ranked.Skip(1).Where(r => r.Entry.Name != best.Entry.Name).ToArray();
            bool streetCollision = streetNames.Contains(best.Entry.Name);
            bool independentStreet = keys.Take(Math.Max(0, keys.Length - 1)).Any(k => NameSimilarity.Score(k, best.Entry.Street) >= .9);
            bool clearName = (!streetCollision || independentStreet) && (best.Score == 1 || others.Length == 0 || best.Score - others[0].Score >= .065);
            if (clearName)
            {
                var same = ranked.Where(r => r.Entry.Name == best.Entry.Name).ToArray();
                bool clearId = same.Length == 1 || best.Street >= .9 && best.Street - same[1].Street >= .1;
                var map = best.Entry.Map;
                result = new(clearId ? map.Id : null, map.Name, clearId ? map.StreetName : "", best.Score);
            }
        }
        if (mapCache.Count > 256) mapCache.Clear();
        mapCache[cacheKey] = result;
        return result;
    }

    private static readonly string[] ProtectedWords = ["red", "blue", "green", "yellow", "white", "black", "purple", "orange", "brown", "pink",
        "dark", "bronze", "steel", "iron", "silver", "gold", "mithril", "adamantium", "orihalcon", "ore", "plate", "crystal", "lesser", "greater", "intermediate"];

    public ItemReading MatchItem(string rawName, string? category = null, IReadOnlySet<int>? observedOnMap = null)
    {
        var key = NameSimilarity.Key(rawName);
        ItemReading unknown = new(rawName, null, 0, "unresolved");
        if (key.Length < 4 || key.Length > 120) return unknown;
        bool Category(CatalogItem i) => category == null || i.Category.Equals(category, StringComparison.OrdinalIgnoreCase);
        bool truncated = rawName.Contains("...", StringComparison.Ordinal) || rawName.Contains('…');
        if (!truncated && exactItems.TryGetValue(key, out var exact))
        {
            var matches = exact.Where(i => Category(i.Item)).ToArray();
            if (matches.Length > 0) return new(matches[0].Item.Name, matches.Length == 1 ? matches[0].Item.Id : null, 1, "catalog exact");
            return unknown;
        }
        var words = Regex.Matches(rawName.ToLowerInvariant(), @"[a-z]+", RegexOptions.None, TimeSpan.FromMilliseconds(100)).Select(m => m.Value).ToHashSet();
        var protectedWords = ProtectedWords.Where(words.Contains).ToArray();
        var candidates = category == null ? itemNames : itemsByCategory.GetValueOrDefault(category, []);
        var ranked = candidates.Where(i =>
                (truncated ? i.Key.Length >= key.Length - 2 : Math.Abs(i.Key.Length - key.Length) <= Math.Max(3, key.Length / 3)) &&
                protectedWords.All(w => Regex.IsMatch(i.Item.Name, @"\b" + w + @"\b", RegexOptions.IgnoreCase)))
            .Select(i => (Entry: i, Score: NameSimilarity.Score(key, truncated ? i.Key[..Math.Min(i.Key.Length, key.Length)] : i.Key)))
            .Where(i => i.Score >= .70)
            .GroupBy(i => i.Entry.Key).Select(g => g.OrderByDescending(i => i.Score).First())
            .OrderByDescending(i => i.Score).ToArray();
        if (ranked.Length == 0) return unknown;
        var best = ranked[0];
        double second = ranked.Length > 1 ? ranked[1].Score : 0;
        bool context = observedOnMap?.Contains(best.Entry.Item.Id) == true;
        double threshold = context && key.Length >= 8 ? .74 : truncated ? .90 : .86;
        double margin = context ? .055 : .08;
        if (best.Score < threshold || best.Score - second < margin || truncated && key.Length < 7) return unknown;
        int? id = exactItems[best.Entry.Key].Count(i => Category(i.Item)) == 1 ? best.Entry.Item.Id : null;
        return new(best.Entry.Item.Name, id, best.Score, context ? "catalog + observed on map" : truncated ? "catalog prefix" : "catalog spelling");
    }
}
