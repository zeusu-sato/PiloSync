using PiloSync.Core;
using PiloSync.Protocol;
namespace PiloSync.Cli;
public static class ScheduleCommand
{
    public static int Run(string[] args, string path, TextWriter output, Func<IReadOnlyList<string>?>? accounts = null)
    {
        var options = CommandLine.ParseSchedule(args);
        if (options.Action is "set" or "clear" or "on" or "off") HotSyncSchedule.Update(path, current =>
        {
            if (current.Legacy) current = current.MigrateLegacy(accounts?.Invoke() ?? throw new ArgumentException("Core\\Path または users.dat がありません"));
            return options.Action is "set" or "clear"
                ? current with { Times = options.Action == "set" ? options.Rows! : [] }
                : current with { Devices = current.Devices.Where(d => d.Name != options.Name).Append(new ScheduleDevice(options.Name!, options.Action == "on" ? ScheduleDeviceState.On : ScheduleDeviceState.Off)).ToArray() };
        });
        var settings = HotSyncSchedule.LoadSettings(path);
        if (settings.Times.Count == 0) output.WriteLine("時刻は未設定です");
        foreach (var row in settings.Times) output.WriteLine(HotSyncSchedule.Format(row.Minutes) + " " + string.Concat(new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday }.Zip(new[] { "月", "火", "水", "木", "金", "土", "日" }).Where(x => row.Days.Contains(x.First)).Select(x => x.Second)));
        foreach (var device in settings.Devices) output.WriteLine($"{device.Name}: {device.State switch { ScheduleDeviceState.On => "オン", ScheduleDeviceState.Off => "オフ", _ => "端末に合わせる" }}");
        return 0;
    }
}
