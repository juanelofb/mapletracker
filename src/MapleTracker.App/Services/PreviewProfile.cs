using System.Text.Json;

namespace MapleTracker.App.Services;

public static class PreviewProfile
{
    public static string Prepare(string sourceDirectory, string destinationDirectory)
    {
        var source=Path.GetFullPath(sourceDirectory);
        var destination=Path.GetFullPath(destinationDirectory);
        if(source.Equals(destination,StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Preview settings must use a separate directory.");
        var destinationFile=Path.Combine(destination,"settings.json");
        if(File.Exists(destinationFile)) return destination;
        var settings=new TrackerSettings();
        var sourceFile=Path.Combine(source,"settings.json");
        if(File.Exists(sourceFile))
        {
            try { settings=JsonSerializer.Deserialize<TrackerSettings>(File.ReadAllText(sourceFile)) ?? settings; }
            catch(JsonException) { }
        }
        settings.UseExperimentalChatFont=false;
        Directory.CreateDirectory(destination);
        var temporary=destinationFile+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            File.WriteAllText(temporary,JsonSerializer.Serialize(settings,new JsonSerializerOptions { WriteIndented=true }));
            File.Move(temporary,destinationFile,false);
        }
        finally { if(File.Exists(temporary)) File.Delete(temporary); }
        return destination;
    }
}
