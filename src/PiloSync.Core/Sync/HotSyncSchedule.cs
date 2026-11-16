using System.Text.Json;
using System.Text.RegularExpressions;
namespace PiloSync.Core;

public static class HotSyncSchedule
{
    public const int MaxTimes = 8;
    private const int MinutesPerDay = 1440;
    private sealed record Document(List<string>? Times);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static string DefaultPath => Path.Combine(AppFolder.Current, "schedule.json");
    public static IReadOnlyList<int> Load(string path)
    {
        try
        {
            var times = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), Options)?.Times;
            if (times is null) return [];
            var minutes = new List<int>();
            foreach (string? text in times)
            {
                if (text is null || !TryParse(text, out int m)) return [];
                minutes.Add(m);
            }
            var sorted = minutes.Distinct().Order().ToArray();
            return sorted.Length > MaxTimes ? [] : sorted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return []; }
    }
    public static void Save(string path, IEnumerable<int> minutes)
    {
        var list = minutes.Distinct().Order().ToArray();
        if (list.Any(m => m < 0 || m >= MinutesPerDay)) throw new ArgumentException("Time must be 0..1439 minutes", nameof(minutes));
        if (list.Length > MaxTimes) throw new ArgumentException($"At most {MaxTimes} times", nameof(minutes));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new Document(list.Select(Format).ToList()), Options));
        File.Move(temp, path, true);
    }
    public static string Format(int minutes) => $"{minutes / 60}:{minutes % 60:00}";
    public static bool TryParse(string text, out int minutes)
    {
        minutes = 0;
        var match = Regex.Match(text.Trim(), "^(?:([0-9]{1,2}):([0-9]{2})|([0-9]{2})([0-9]{2}))$");
        if (!match.Success) return false;
        int hour = int.Parse(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[3].Value);
        int minute = int.Parse(match.Groups[2].Success ? match.Groups[2].Value : match.Groups[4].Value);
        if (hour > 23 || minute > 59) return false;
        minutes = hour * 60 + minute;
        return true;
    }
}
