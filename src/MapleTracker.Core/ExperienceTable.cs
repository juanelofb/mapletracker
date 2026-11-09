using System.Text.Json;

namespace MapleTracker.Core;

public sealed class ExperienceTable
{
    private readonly Dictionary<int, long> required;
    public ExperienceTable(Dictionary<int, long> values)
    {
        if (values.Count != 99 || Enumerable.Range(1, 99).Any(i => !values.TryGetValue(i, out var n) || n <= 0))
            throw new InvalidDataException("EXP table must contain positive values for levels 1–99.");
        required = values;
    }
    public static ExperienceTable Load(string path) => new(JsonSerializer.Deserialize<Dictionary<int, long>>(File.ReadAllText(path)) ?? []);
    public long? Required(int level) => required.TryGetValue(level, out var n) ? n : null;
    public long Absolute(int level, long experience)
    {
        if (level is < 1 or > 100 || experience < 0 || (Required(level) is long max && experience >= max))
            throw new ArgumentOutOfRangeException(nameof(experience));
        return Enumerable.Range(1, level - 1).Sum(i => required[i]) + experience;
    }
}
