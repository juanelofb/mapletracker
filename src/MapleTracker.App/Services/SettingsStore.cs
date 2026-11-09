using System.Text.Json;

namespace MapleTracker.App.Services;

public sealed class SettingsStore
{
    public static string DataDirectory => Path.Combine(AppContext.BaseDirectory, "data");
    private static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public string? Warning { get; private set; }
    public TrackerSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new();
            var value = JsonSerializer.Deserialize<TrackerSettings>(File.ReadAllText(SettingsPath)) ?? throw new JsonException("Empty settings.");
            if (value.Chat == null || value.Loot == null || value.Level == null || value.Experience == null ||
                !value.Chat.IsValid || !value.Loot.IsValid || !value.Level.IsValid || !value.Experience.IsValid ||
                value.BuyingTerms == null || value.SellingTerms == null) throw new JsonException("Invalid regions or keywords.");
            value.PollMilliseconds = Math.Clamp(value.PollMilliseconds, 250, 5000);
            value.Opacity = double.IsFinite(value.Opacity) ? Math.Clamp(value.Opacity, .4, 1) : 1;
            return value;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Warning = "Settings could not be loaded; defaults were restored. " + e.Message;
            return new();
        }
    }
    public void Save(TrackerSettings settings)
    {
        Directory.CreateDirectory(DataDirectory);
        var temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, SettingsPath, true);
    }
}
