using System.Text.Json;
using System.Text.RegularExpressions;
namespace PiloSync.Core;

public enum ScheduleDeviceState { Off, On, Inherit }
public sealed record ScheduleTime(int Minutes, IReadOnlyList<DayOfWeek> Days);
public sealed record ScheduleDevice(string Name, ScheduleDeviceState State);
public sealed record ScheduleSettings(IReadOnlyList<ScheduleTime> Times, IReadOnlyList<ScheduleDevice> Devices, bool Legacy = false)
{
    public ScheduleDeviceState StateFor(string name) => Devices.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.Ordinal))?.State ?? ScheduleDeviceState.Off;
    public ScheduleSettings MigrateLegacy(IEnumerable<string> accountNames) => !Legacy ? this : this with { Devices = accountNames.Distinct(StringComparer.Ordinal).Select(n => new ScheduleDevice(n, ScheduleDeviceState.Inherit)).ToArray(), Legacy = false };
}

public static class HotSyncSchedule
{
    public const int MaxTimes = 8;
    private const int MinutesPerDay = 1440;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static string DefaultPath => Path.Combine(AppFolder.Current, "schedule.json");
    private static readonly string[] DayNames = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];
    public static int DayIndex(DayOfWeek day) => (int)day;
    public static ScheduleSettings LoadSettings(string path)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var root = json.RootElement;
            var times = root.GetProperty("times");
            bool legacy = !root.TryGetProperty("version", out var version);
            if (!legacy && version.GetInt32() != 2) return Empty();
            var rows = new List<ScheduleTime>();
            foreach (var row in times.EnumerateArray())
            {
                string? value = legacy ? row.GetString() : row.GetProperty("time").GetString();
                if (value is null || !TryParse(value, out int minute)) return Empty();
                var days = new List<DayOfWeek>();
                if (legacy) days.AddRange(Enum.GetValues<DayOfWeek>());
                else foreach (var item in row.GetProperty("days").EnumerateArray())
                {
                    int day = Array.IndexOf(DayNames, item.GetString());
                    if (day < 0) return Empty();
                    days.Add((DayOfWeek)day);
                }
                rows.Add(new(minute, days));
            }
            var devices = new List<ScheduleDevice>();
            if (!legacy) foreach (var device in root.GetProperty("devices").EnumerateArray())
            {
                string? name = device.GetProperty("name").GetString();
                string? state = device.GetProperty("state").GetString();
                if (string.IsNullOrEmpty(name) || state is not ("on" or "off" or "inherit")) return Empty();
                devices.Add(new(name, state switch { "on" => ScheduleDeviceState.On, "inherit" => ScheduleDeviceState.Inherit, _ => ScheduleDeviceState.Off }));
            }
            return Normalize(new(rows, devices, legacy));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or OverflowException) { return Empty(); }
    }
    private static ScheduleSettings Empty() => new([], []);
    private static ScheduleSettings Normalize(ScheduleSettings settings)
    {
        if (settings.Times.Any(t => t.Minutes < 0 || t.Minutes >= MinutesPerDay || t.Days.Any(d => (int)d < 0 || (int)d > 6))) throw new ArgumentException("Invalid schedule");
        var rows = settings.Times.GroupBy(t => t.Minutes).OrderBy(g => g.Key).Select(g => new ScheduleTime(g.Key, g.SelectMany(t => t.Days).Distinct().OrderBy(DayIndex).ToArray())).ToArray();
        if (Enumerable.Range(0, 7).Any(d => rows.Count(t => t.Days.Contains((DayOfWeek)d)) > MaxTimes)) throw new ArgumentException("At most 8 times per day");
        if (settings.Devices.Any(d => string.IsNullOrEmpty(d.Name) || !Enum.IsDefined(d.State)) || settings.Devices.Select(d => d.Name).Distinct(StringComparer.Ordinal).Count() != settings.Devices.Count) throw new ArgumentException("Invalid devices");
        return settings with { Times = rows };
    }
    public static void SaveSettings(string path, ScheduleSettings settings) => Update(path, _ => settings);
    public static ScheduleSettings Update(string path, Func<ScheduleSettings, ScheduleSettings> change)
    {
        string fullPath = Path.GetFullPath(path);
        string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath)));
        using var mutex = new Mutex(false, "PiloSyncSchedule_" + key);
        try { mutex.WaitOne(); } catch (AbandonedMutexException) { }
        try
        {
            var changed = change(LoadSettings(fullPath));
            if (changed.Legacy) throw new InvalidOperationException("Legacy schedule must be migrated before saving");
            var result = Normalize(changed);
            var document = new { version = 2, times = result.Times.Select(t => new { time = Format(t.Minutes), days = t.Days.Select(d => DayNames[DayIndex(d)]) }), devices = result.Devices.Select(d => new { name = d.Name, state = d.State.ToString().ToLowerInvariant() }) };
            string json = JsonSerializer.Serialize(document, Options);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            string temp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temp, json); File.Move(temp, fullPath, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return result;
        }
        finally { mutex.ReleaseMutex(); }
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
