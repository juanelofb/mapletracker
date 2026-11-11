using System.Text.Json;

namespace MapleTracker.Core;

public sealed record CatalogSnapshot
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public int SchemaVersion { get; init; }
    public string DataUpdatedAt { get; init; } = "";
    public string ClientBuild { get; init; } = "";
    public string Source { get; init; } = "";
    public string Attribution { get; init; } = "";
    public string License { get; init; } = "";
    public string Changes { get; init; } = "";
    public Dictionary<string, string> SourceHashes { get; init; } = [];
    public CatalogItem[] Items { get; init; } = [];
    public CatalogMap[] Maps { get; init; } = [];
    public CatalogMonster[] Monsters { get; init; } = [];

    public static CatalogSnapshot Read(Stream stream)
    {
        var snapshot = JsonSerializer.Deserialize<CatalogSnapshot>(stream, JsonOptions)
            ?? throw new InvalidDataException("Missing world catalog.");
        snapshot.Validate();
        return snapshot;
    }

    public Task WriteAsync(Stream stream, CancellationToken cancellationToken) =>
        JsonSerializer.SerializeAsync(stream, this, JsonOptions, cancellationToken);

    public GameCatalog BuildCatalog() => new(Items, Maps, Monsters, DataUpdatedAt);

    public void Validate()
    {
        if (SchemaVersion != 1 || Items is not { Length: > 0 } || Maps is not { Length: > 0 } || Monsters == null ||
            Items.Any(i => i == null || string.IsNullOrWhiteSpace(i.Name) || string.IsNullOrWhiteSpace(i.Category)) ||
            Maps.Any(m => m == null || m.Name == null || m.StreetName == null || m.MobIds == null) ||
            Monsters.Any(m => m == null || string.IsNullOrWhiteSpace(m.Name)) ||
            Items.Select(i => i.Id).Distinct().Count() != Items.Length ||
            Maps.Select(m => m.Id).Distinct().Count() != Maps.Length ||
            Monsters.Select(m => m.Id).Distinct().Count() != Monsters.Length)
            throw new InvalidDataException("Invalid world catalog.");
    }
}
