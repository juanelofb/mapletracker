using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MapleTracker.App.Services;
using MapleTracker.Core;

namespace MapleTracker.AppChecks;

internal static class CatalogRefreshChecks
{
    public static void Run(Action<bool, string> check)
    {
        var bundled = RuntimeAssets.BundledCatalog;
        var directory = Directory.CreateTempSubdirectory("mapletracker-catalog-checks-");
        try
        {
            VerifyStartupRefresh(directory.FullName, bundled, check);
            var fixture = Environment.GetEnvironmentVariable("MAPLETRACKER_OSMS_FIXTURES");
            if (!string.IsNullOrEmpty(fixture))
                VerifyRecordedApi(Path.Combine(directory.FullName, "recorded-api"), fixture, bundled, check);
        }
        finally { directory.Delete(recursive: true); }
    }

    private static void VerifyStartupRefresh(string directory, CatalogSnapshot bundled, Action<bool, string> check)
    {
        using var api = new CatalogApi();
        using var http = new HttpClient(api, disposeHandler: false);
        var cachePath = Path.Combine(directory, "world-catalog.json");
        var bundledDate = DateTimeOffset.Parse(bundled.DataUpdatedAt, System.Globalization.CultureInfo.InvariantCulture);
        api.Metadata = Manifest(bundled, bundledDate);
        var store = new WorldCatalogStore(directory, bundled, http);
        check(store.RefreshAsync().GetAwaiter().GetResult() == null && File.Exists(cachePath) &&
            api.Requests.SequenceEqual(new[] { "meta.json" }), "first launch saves the bundled catalog when API hashes match");

        const string newName = "Verdigris Practice Pendant";
        var newItem = new CatalogItem(99999999, newName, "Etc");
        var items = bundled.Items.Append(newItem).ToArray();
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(items, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        api.Metadata = Manifest(bundled, bundledDate.AddDays(1), payload, items.Length);
        api.Payloads["items.json"] = payload;
        api.Requests.Clear();
        var current = store.LoadCatalog();
        var updated = store.RefreshAsync().GetAwaiter().GetResult();
        check(updated?.MatchItem("Verdigris Practlce Pendant", "Etc").Id == newItem.Id &&
            api.Requests.SequenceEqual(new[] { "meta.json", "items.json" }), "startup fetches only the changed list and matches a new item");

        var controller = new TrackerController(new TrackerSettings(), current);
        try
        {
            controller.AddLoot("Verdigris Practlce Pendant x3 earned. (Etc)");
            controller.AddLoot("You have gained mesos (+12)");
            controller.ApplyCatalog(updated!);
            check(controller.Items.TryGetValue(newName, out var total) && total == 3 &&
                controller.Snapshot.Mesos == 12, "applying the catalog rechecks item totals and preserves session counts");
        }
        finally { controller.Shutdown().GetAwaiter().GetResult(); }

        byte[] saved = File.ReadAllBytes(cachePath);
        store = new WorldCatalogStore(directory, bundled, http);
        api.Requests.Clear();
        check(store.LoadCatalog().ContainsItem(newItem.Id) && store.RefreshAsync().GetAwaiter().GetResult() == null &&
            api.Requests.SequenceEqual(new[] { "meta.json" }) && File.ReadAllBytes(cachePath).SequenceEqual(saved),
            "later launches use the saved catalog and skip unchanged downloads");

        byte[] corrupt = (byte[])payload.Clone();
        corrupt[^1] ^= 1;
        api.Metadata = Manifest(bundled, bundledDate.AddDays(2), corrupt, items.Length);
        check(store.RefreshAsync().GetAwaiter().GetResult() == null && File.ReadAllBytes(cachePath).SequenceEqual(saved),
            "failed checksum leaves the last good snapshot intact");

        api.Metadata = null;
        store = new WorldCatalogStore(directory, bundled, http);
        check(store.LoadCatalog().ContainsItem(newItem.Id) && store.RefreshAsync().GetAwaiter().GetResult() == null,
            "offline launch still uses the latest local catalog");
        File.WriteAllText(cachePath, "{");
        check(new WorldCatalogStore(directory, bundled, http).LoadCatalog().Items.Count == bundled.Items.Length,
            "unreadable local catalog falls back to the embedded snapshot");
    }

    private static void VerifyRecordedApi(string directory, string fixture, CatalogSnapshot bundled, Action<bool, string> check)
    {
        using var api = new CatalogApi { Metadata = File.ReadAllBytes(Path.Combine(fixture, "api-meta.json")) };
        foreach (var name in new[] { "items.json", "maps.json", "monsters.json" })
            api.Payloads[name] = File.ReadAllBytes(Path.Combine(fixture, name));
        using var http = new HttpClient(api, disposeHandler: false);
        var store = new WorldCatalogStore(directory, bundled with { SourceHashes = [], DataUpdatedAt = "" }, http);
        var updated = store.RefreshAsync().GetAwaiter().GetResult();
        var map = updated?.MatchMap(["Victoria Road", "Kerning City Construction Site"]);
        check(updated?.Items.Count == 2259 && updated.Maps.Count == 336 && map?.Id == 10003010 &&
            updated.MonsterNames(map.Id).Length > 0 && api.Requests.Count == 4,
            "recorded OSMS payloads refresh all lists and preserve map placements");
    }

    private static byte[] Manifest(CatalogSnapshot bundled, DateTimeOffset date, byte[]? items = null, int itemCount = 0)
    {
        var counts = new Dictionary<string, int>
        {
            ["items.json"] = bundled.Items.Length,
            ["maps.json"] = bundled.Maps.Length,
            ["monsters.json"] = bundled.Monsters.Length
        };
        var files = counts.ToDictionary(p => p.Key, p => new { sha256 = bundled.SourceHashes[p.Key], bytes = 1, records = p.Value });
        if (items != null)
            files["items.json"] = new { sha256 = Convert.ToHexString(SHA256.HashData(items)), bytes = items.Length, records = itemCount };
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = 1, client_build = bundled.ClientBuild, data_updated_at = date, files
        });
    }

    private sealed class CatalogApi : HttpMessageHandler
    {
        public byte[]? Metadata { get; set; }
        public Dictionary<string, byte[]> Payloads { get; } = [];
        public ConcurrentQueue<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string file = request.RequestUri!.Segments[^1];
            Requests.Enqueue(file);
            var bytes = file == "meta.json" ? Metadata : Payloads.GetValueOrDefault(file);
            return Task.FromResult(bytes == null
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
