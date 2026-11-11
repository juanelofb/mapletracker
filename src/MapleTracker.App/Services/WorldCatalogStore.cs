using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace MapleTracker.App.Services;

internal sealed class WorldCatalogStore
{
    private static readonly Uri Api = new("https://osmsdataexplorer.com/data/api/v1/");
    private static readonly string[] Files = ["items.json", "maps.json", "monsters.json"];
    private const int MaxFileBytes = 16 * 1024 * 1024;
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false });
    private static readonly JsonSerializerOptions ApiJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private sealed record ApiMetadata(int SchemaVersion, string ClientBuild, DateTimeOffset DataUpdatedAt, Dictionary<string, ApiFile> Files);
    private sealed record ApiFile(string Sha256, int Bytes, int Records);
    private sealed record ApiMap(int Id, string Name, string? StreetName, ApiPlacement[]? Mobs);
    private sealed record ApiPlacement(int MobId);

    private readonly string cachePath;
    private readonly HttpClient http;
    private CatalogSnapshot snapshot;

    internal WorldCatalogStore(string directory, CatalogSnapshot bundled, HttpClient? http = null)
    {
        cachePath = Path.Combine(directory, "world-catalog.json");
        this.http = http ?? Client;
        var cached = ReadCache();
        snapshot = cached != null && UpdatedAt(cached) >= UpdatedAt(bundled) ? cached : bundled;
    }

    internal GameCatalog LoadCatalog() => snapshot.BuildCatalog();

    internal async Task<GameCatalog?> RefreshAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        try
        {
            var bytes = await DownloadAsync("meta.json", 128 * 1024, token).ConfigureAwait(false);
            var metadata = JsonSerializer.Deserialize<ApiMetadata>(bytes, ApiJson);
            if (metadata == null || metadata.SchemaVersion != 1 || string.IsNullOrWhiteSpace(metadata.ClientBuild) ||
                metadata.DataUpdatedAt == default || metadata.Files == null ||
                Files.Any(name => !metadata.Files.TryGetValue(name, out var file) || file == null ||
                    !IsHash(file.Sha256) || file.Bytes is <= 0 or > MaxFileBytes || file.Records <= 0))
                throw new InvalidDataException("Invalid OSMS catalog metadata.");

            if (metadata.DataUpdatedAt < UpdatedAt(snapshot)) return null;

            var items = LoadPartAsync("items.json", snapshot.Items, metadata.Files["items.json"], token);
            var monsters = LoadPartAsync("monsters.json", snapshot.Monsters, metadata.Files["monsters.json"], token);
            var maps = LoadMapsAsync(metadata.Files["maps.json"], token);
            await Task.WhenAll(items, monsters, maps).ConfigureAwait(false);
            var next = snapshot with
            {
                ClientBuild = metadata.ClientBuild,
                DataUpdatedAt = metadata.DataUpdatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                SourceHashes = Files.ToDictionary(name => name, name => metadata.Files[name].Sha256),
                Items = await items.ConfigureAwait(false),
                Monsters = await monsters.ConfigureAwait(false),
                Maps = await maps.ConfigureAwait(false)
            };
            next.Validate();
            var saved = await SaveAsync(next, token).ConfigureAwait(false);
            var changed = !SameRevision(snapshot, saved);
            snapshot = saved;
            return changed ? snapshot.BuildCatalog() : null;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException or
            JsonException or InvalidDataException or OperationCanceledException)
        {
            Debug.WriteLine($"OSMS catalog refresh skipped: {error.Message}");
            return null;
        }
    }

    private bool CanReuse(string name, int count, ApiFile file) =>
        count == file.Records && snapshot.SourceHashes.TryGetValue(name, out var hash) &&
        string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase);

    private Task<T[]> LoadPartAsync<T>(string name, T[] rows, ApiFile file, CancellationToken token) where T : class =>
        CanReuse(name, rows.Length, file) ? Task.FromResult(rows) : DownloadRowsAsync<T>(name, file, token);

    private async Task<CatalogMap[]> LoadMapsAsync(ApiFile file, CancellationToken token)
    {
        if (CanReuse("maps.json", snapshot.Maps.Length, file)) return snapshot.Maps;
        var rows = await DownloadRowsAsync<ApiMap>("maps.json", file, token).ConfigureAwait(false);
        if (rows.Any(m => m.Mobs?.Any(mob => mob == null) == true))
            throw new InvalidDataException("Invalid OSMS map placements.");
        return rows.Select(m => new CatalogMap(m.Id, m.Name, m.StreetName ?? "",
            m.Mobs?.Select(mob => mob.MobId).Distinct().Order().ToArray() ?? [])).ToArray();
    }

    private async Task<T[]> DownloadRowsAsync<T>(string name, ApiFile file, CancellationToken token) where T : class
    {
        var bytes = await DownloadAsync(name, file.Bytes, token).ConfigureAwait(false);
        if (bytes.Length != file.Bytes ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"OSMS {name} did not match its published hash and size.");

        var rows = JsonSerializer.Deserialize<T[]>(bytes, ApiJson);
        if (rows == null || rows.Length != file.Records || rows.Any(row => row == null))
            throw new InvalidDataException($"OSMS {name} did not match its published record count.");
        return rows;
    }

    private async Task<byte[]> DownloadAsync(string name, int maxBytes, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Api, name));
        request.Headers.UserAgent.ParseAdd("MapleTracker/1.1");
        if (name == "meta.json") request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException($"OSMS {name} is too large.");

        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var content = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (content.Length + read > maxBytes) throw new InvalidDataException($"OSMS {name} is too large.");
            content.Write(buffer, 0, read);
        }
        return content.ToArray();
    }

    private CatalogSnapshot? ReadCache()
    {
        try
        {
            if (!File.Exists(cachePath)) return null;
            using var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > 3 * MaxFileBytes) return null;
            var cached = CatalogSnapshot.Read(stream);
            return UpdatedAt(cached) != default && !string.IsNullOrWhiteSpace(cached.ClientBuild) && cached.SourceHashes != null &&
                Files.All(name => cached.SourceHashes.TryGetValue(name, out var hash) && IsHash(hash)) ? cached : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Debug.WriteLine($"OSMS catalog cache skipped: {error.Message}");
            return null;
        }
    }

    private async Task<CatalogSnapshot> SaveAsync(CatalogSnapshot next, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        // Serialize the check and replacement across app instances. Readers always see one complete snapshot.
        using var cacheLock = new FileStream(cachePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, 1, FileOptions.DeleteOnClose);
        var cached = ReadCache();
        if (cached != null && (UpdatedAt(cached) > UpdatedAt(next) || SameRevision(cached, next))) return cached;

        var temporary = $"{cachePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                await next.WriteAsync(stream, token).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();
            File.Move(temporary, cachePath, overwrite: true);
            return next;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(char.IsAsciiHexDigit);

    private static DateTimeOffset UpdatedAt(CatalogSnapshot data) =>
        DateTimeOffset.TryParse(data.DataUpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : default;

    private static bool SameRevision(CatalogSnapshot a, CatalogSnapshot b) =>
        UpdatedAt(a) == UpdatedAt(b) && a.ClientBuild == b.ClientBuild &&
        a.Items.Length == b.Items.Length && a.Maps.Length == b.Maps.Length && a.Monsters.Length == b.Monsters.Length &&
        Files.All(name => string.Equals(a.SourceHashes.GetValueOrDefault(name), b.SourceHashes.GetValueOrDefault(name), StringComparison.OrdinalIgnoreCase));
}
