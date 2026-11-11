using MapleTracker.Core;

internal static class WorldCatalogChecks
{
    public static void Run(Action<bool, string> check)
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "world-catalog.json"));
        var catalog = GameCatalog.Load(stream);
        check(catalog.Items.Count == 2259 && catalog.Maps.Count == 336, "bundled OSMS catalog loads all map and item names");
        check(catalog.MatchMap(["Victoria Road", "Keming City Construction Ste"])?.Name == "Kerning City Construction Site", "misspelled map title resolves to canonical map");
        var construction = catalog.MatchMap(["Kerning City Construction Site"])!;
        check(construction.Id == 10003010 && catalog.MonsterNames(construction.Id).Length == 5, "canonical map links to its monster placements");
        foreach (var suffix in new[] { "I", "II", "III", "IV" })
            check(catalog.MatchMap(["Dungeon", "Ant Tunnel " + suffix])?.Name == "Ant Tunnel " + suffix, "map suffix remains distinct: " + suffix);
        check(catalog.MatchMap(["Dungeon", "Ant Tunnel ill"])?.Name == "Ant Tunnel III", "Roman numeral OCR glyphs retain their count");
        check(catalog.MatchMap(["Nothing readable here"]) == null, "unrecognized map stays unknown");
        check(catalog.MatchMap(["Victoria Road"]) == null, "street-only OCR cannot impersonate a map with the same name");
        check(catalog.MatchMap(["Yellow Mushroom House", "Victoria Road"])?.Id == 10001008, "separate street text resolves a street/map name collision");
        check(catalog.MatchMap(["Mushroom Town"]) is { Id: null, Name: "Mushroom Town", StreetName: "" }, "duplicate map names do not invent a map ID or street");
        check(catalog.MatchItem("Squishy Liguid", "Etc").Name == "Squishy Liquid", "one OCR letter error resolves to catalog item");
        check(catalog.MatchItem("Horny Mushroo...", "Etc").Name == "Horny Mushroom Cap", "unique truncated mushroom loot expands to full name");
        check(catalog.MatchItem("Charm of the U...", "Etc").Name == "Charm of the Undead", "unique truncated charm loot expands to full name");
        check(catalog.MatchItem("Glowing Mu...", "Etc").Name == "Glowing Mu...", "ambiguous truncated loot stays unresolved");
        check(catalog.MatchItem("Power Crystal...", "Etc").Name == "Power Crystal...", "truncated exact prefix cannot hide a different item such as Crystal Ore");
        check(catalog.MatchItem("Blue Snail Shel", "Etc").Name == "Blue Snail Shell", "color variant is preserved while spelling is corrected");
        check(catalog.MatchItem("Squishy Liquid", "Equipment").Id == null, "item category prevents an incorrect cross-category match");
        check(catalog.MatchItem("Red Poion", "Consumable") is { Name: "Red Potion", Id: null }, "duplicate item names canonicalize without guessing an ID");
        check(catalog.MatchItem("Unknown Custom Relic").Name == "Unknown Custom Relic", "unknown item is retained verbatim");

        var counter = new LootCounter(catalog);
        counter.Add(new("Squishy Liquid", 19), "Squishy Liquid x19 earned. (Etc)", construction.Id);
        counter.Add(new("Squishy Liguid", 5), "Squishy Liguid x5 earned. (Etc)", construction.Id);
        check(counter.Totals.Count == 1 && counter.Totals["Squishy Liquid"] == 24, "19 correct + 5 misspelled pickups become one total of 24");
        counter.Add(new("Unknown Custom Relic", 3), "Unknown Custom Relic x3 earned. (Etc)", null);
        check(counter.Totals.Values.Sum() == 27, "normalization preserves every pickup quantity");

        const string noisy = "Squishv Liqaid";
        check(catalog.MatchItem(noisy, "Etc").Id == null, "less certain spelling requires map evidence");
        counter = new LootCounter(catalog);
        int map = construction.Id!.Value;
        counter.Add(new(noisy, 5), noisy + " x5 earned. (Etc)", map);
        counter.Add(new(noisy, 7), noisy + " x7 earned. (Etc)", 10005060);
        counter.Add(new(noisy, 3), noisy + " x3 earned. (Etc)", null);
        counter.Add(new("Squishy Liquid", 1), "Squishy Liquid x1 earned. (Etc)", map);
        check(counter.LearnedItems.Count == 0, "one confirmed pickup does not establish map context");
        int revision = counter.Revision;
        counter.Add(new("Squishy Liquid", 1), "Squishy Liquid x1 earned. (Etc)", map);
        check(counter.LearnedItems[map].Contains(4000006), "two strongly matched pickups establish an observed-on-map hint");
        check(counter.Totals["Squishy Liquid"] == 7 && counter.Totals[noisy] == 10 && counter.Revision > revision,
            "new map evidence reconciles only pickups originally observed on that map");
        var restored = new LootCounter(catalog, counter.LearnedItems);
        check(restored.Add(new(noisy, 2), noisy + " x2 earned. (Etc)", map).Name == "Squishy Liquid", "persisted map hints resolve later uncertain spelling");
        check(restored.Add(new(noisy, 4), noisy + " x4 earned. (Etc)", 10005060).Name == noisy, "other maps do not inherit a map-specific hint");
        restored.Reset();
        check(restored.Totals.Count == 0 && restored.LearnedItems[map].Contains(4000006), "reset clears loot counts while retaining learned map hints");

        var context = new MapContextTracker();
        context.Observe(construction, TimeSpan.Zero);
        check(context.LootMapId == null, "first map observation waits for confirmation");
        context.Observe(construction, TimeSpan.FromSeconds(.5));
        check(context.LootMapId == map && context.Title == construction.Name, "two observations confirm map title and context");
        var ant = catalog.MatchMap(["Ant Tunnel III"])!;
        context.Observe(ant, TimeSpan.FromSeconds(1));
        check(context.LootMapId == null, "map transition immediately disables old-map hints");
        context.Observe(ant, TimeSpan.FromSeconds(1.5));
        check(context.LootMapId == ant.Id, "map transition confirms the new map independently");
        context.Observe(null, TimeSpan.FromSeconds(2));
        check(context.LootMapId == null, "hidden minimap immediately disables hints");
        context.Observe(null, TimeSpan.FromSeconds(7));
        check(context.Current == null, "stale map title expires after five unreadable seconds");
    }
}
