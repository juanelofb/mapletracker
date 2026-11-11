namespace MapleTracker.Core;

public sealed class MapContextTracker
{
    public MapReading? Current { get; private set; }
    public int? LootMapId => reliable ? Current?.Id : null;
    public string Title => reliable && Current != null ? Current.Name : Current != null ? "Reading map…" : "Waiting for map";
    private MapReading? pending;
    private int confirmations;
    private bool reliable;
    private TimeSpan lastRead;
    private static string Identity(MapReading map) => map.Id?.ToString() ?? map.StreetName + "|" + map.Name;

    public void Observe(MapReading? reading, TimeSpan now)
    {
        if (reading == null)
        {
            pending = null; confirmations = 0; reliable = false;
            if (now - lastRead >= TimeSpan.FromSeconds(5)) Current = null;
            return;
        }
        lastRead = now;
        if (Current != null && Identity(Current) == Identity(reading))
        { Current = reading; reliable = true; pending = null; confirmations = 0; return; }
        reliable = false;
        if (pending != null && Identity(pending) == Identity(reading)) confirmations++;
        else { pending = reading; confirmations = 1; }
        if (confirmations >= 2) { Current = reading; reliable = true; pending = null; confirmations = 0; }
    }
    public void Reset() { Current = null; pending = null; reliable = false; confirmations = 0; lastRead = TimeSpan.Zero; }
}
