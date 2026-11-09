namespace MapleTracker.Core;

public sealed record SessionSnapshot(int? Level, long? Experience, long? Required, long GainedExperience, long Mesos,
    double ExperiencePerMinute, double MesosPerMinute, double RecentExperiencePerMinute, TimeSpan? Eta, TimeSpan ActiveTime);

public sealed class SessionTracker(ExperienceTable table)
{
    private long? baseline;
    private long total;
    private long mesos;
    private HudReading? current;
    private readonly List<(TimeSpan Time, long Xp)> samples = [];
    public void Reset() { baseline = null; total = 0; mesos = 0; current = null; samples.Clear(); }
    public void ResumeAt(HudReading reading)
    {
        // Keep earned EXP, but exclude progress made while statistics were stopped.
        baseline = table.Absolute(reading.Level, reading.Experience) - total;
        current = reading;
        samples.Clear();
    }
    public bool Observe(HudReading reading, TimeSpan activeTime)
    {
        if (current != null && Math.Abs(reading.Level - current.Level) > 1) return false;
        var absolute = table.Absolute(reading.Level, reading.Experience);
        baseline ??= absolute;
        current = reading;
        total = absolute - baseline.Value;
        return true;
    }
    public void AddMesos(long amount) { if (amount > 0) mesos = checked(mesos + amount); }
    public SessionSnapshot Snapshot(TimeSpan activeTime)
    {
        if (current != null)
        {
            if (samples.Count == 0 || activeTime > samples[^1].Time) samples.Add((activeTime, total));
            // Keep the boundary sample immediately preceding the rolling window.
            while (samples.Count > 2 && samples[1].Time < activeTime - TimeSpan.FromMinutes(5)) samples.RemoveAt(0);
        }
        var minutes = activeTime.TotalMinutes;
        var rate = minutes > 0 ? total / minutes : 0;
        var recent = samples.Count >= 2 && (activeTime - samples[0].Time).TotalSeconds > 0
            ? (total - samples[0].Xp) / (activeTime - samples[0].Time).TotalMinutes : 0;
        var required = current == null ? null : table.Required(current.Level);
        var etaMinutes = required != null && current != null && recent > 0 ? (required.Value - current.Experience) / recent : (double?)null;
        TimeSpan? eta = minutes >= .25 && etaMinutes is >= 0 and < 5256000 ? TimeSpan.FromMinutes(etaMinutes.Value) : null;
        return new(current?.Level, current?.Experience, required, total, mesos, rate, minutes > 0 ? mesos / minutes : 0, recent, eta, activeTime);
    }
}
