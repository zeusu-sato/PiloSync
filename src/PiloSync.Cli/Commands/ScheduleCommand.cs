using PiloSync.Core;

namespace PiloSync.Cli;
public static class ScheduleCommand
{
    public static int Run(string[] args, string path, TextWriter output)
    {
        var options = CommandLine.ParseSchedule(args);
        if (options.Action == "set") HotSyncSchedule.Save(path, options.Minutes);
        else if (options.Action == "clear") HotSyncSchedule.Save(path, []);
        var times = HotSyncSchedule.Load(path);
        output.WriteLine(times.Count == 0 ? "時刻は未設定です" : string.Join(", ", times.Select(HotSyncSchedule.Format)));
        return 0;
    }
}
