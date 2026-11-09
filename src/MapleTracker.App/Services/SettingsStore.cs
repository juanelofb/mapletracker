using System.Text.Json;

namespace MapleTracker.App.Services;

public sealed class SettingsStore
{
    public static string DataDirectory => AppContext.GetData("MapleTracker.DataDirectory") as string
        ?? Environment.GetEnvironmentVariable("MAPLETRACKER_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MapleTracker");
    private static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public string? Warning { get; private set; }
    public TrackerSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                // An EXE placed in a previous release folder can import its settings once.
                var legacy = Path.Combine(AppContext.BaseDirectory, "data", "settings.json");
                if (!File.Exists(legacy)) return new();
                Directory.CreateDirectory(DataDirectory);
                File.Copy(legacy, SettingsPath, false);
            }
            var json = File.ReadAllText(SettingsPath);
            var value = JsonSerializer.Deserialize<TrackerSettings>(json) ?? throw new JsonException("Empty settings.");
            if (value.Chat == null || value.Loot == null || value.Level == null || value.Experience == null ||
                !value.Chat.IsValid || !value.Loot.IsValid || !value.Level.IsValid || !value.Experience.IsValid ||
                value.BuyingTerms == null || value.SellingTerms == null) throw new JsonException("Invalid regions or keywords.");
            value.PollMilliseconds = Math.Clamp(value.PollMilliseconds, 250, 5000);
            if (!Enum.IsDefined(value.RecognitionQuality)) value.RecognitionQuality = RecognitionQuality.Balanced;
            value.Opacity = double.IsFinite(value.Opacity) ? Math.Clamp(value.Opacity, .4, 1) : 1;
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(nameof(TrackerSettings.ChatOpacity), out _)) value.ChatOpacity = value.Opacity;
            if (!document.RootElement.TryGetProperty(nameof(TrackerSettings.StatisticsOpacity), out _)) value.StatisticsOpacity = value.Opacity;
            value.ChatOpacity = double.IsFinite(value.ChatOpacity) ? Math.Clamp(value.ChatOpacity, .4, 1) : .9;
            value.StatisticsOpacity = double.IsFinite(value.StatisticsOpacity) ? Math.Clamp(value.StatisticsOpacity, .4, 1) : .9;
            value.BuyingItems ??= [];
            value.SellingItems ??= [];
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
