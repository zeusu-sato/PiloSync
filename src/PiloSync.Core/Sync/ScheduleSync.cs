using PiloSync.Core;
namespace PiloSync.Protocol;

public static class ScheduleSync
{
    public const string Creator = "PSas";
    public const ushort Version = 1;
    public const int Size = 128;
    private const int TimesOffset = 14, EnabledOffset = 126;
    public static ScheduleDeviceState? Apply(DlpClient dlp, ScheduleSettings schedule, ScheduleDeviceState state, Action<string> log)
    {
        if (!schedule.Times.Any(t => t.Days.Count > 0)) return null;
        ScheduleDeviceState? resolved = null;
        try
        {
            var preference = dlp.ReadAppPreference(Creator, 0, maxBytes: Size);
            if (preference is null) return null;
            if (preference.Version != Version || preference.ActualSize != Size || preference.Data.Length != Size)
            {
                log($"HotSync の時刻を設定しませんでした：PSas の版 {preference.Version}、サイズ {preference.ActualSize} は未対応です");
                return null;
            }
            if (state == ScheduleDeviceState.Inherit) resolved = state = Get(preference.Data, EnabledOffset) != 0 ? ScheduleDeviceState.On : ScheduleDeviceState.Off;
            bool write = state == ScheduleDeviceState.On ? !Matches(preference.Data, schedule) : Get(preference.Data, EnabledOffset) != 0;
            if (write)
            {
                byte[] data = state == ScheduleDeviceState.On ? Build(schedule) : (byte[])preference.Data.Clone();
                if (state == ScheduleDeviceState.Off) Put(data, EnabledOffset, 0);
                dlp.WriteAppPreference(Creator, 0, Version, data);
            }
            var device = dlp.ReadSysDateTime(); var pc = DateTime.Now;
            dlp.SetSysDateTime(pc);
            int skew = Skew(device, pc); string seconds = skew.ToString("+0;-0;+0");
            if (write && state == ScheduleDeviceState.Off) log($"HotSync の時刻をオフ / 時計のずれ {seconds} 秒");
            else if (write) log($"HotSync の時刻を設定 {string.Join(", ", schedule.Times.Where(t => t.Days.Count > 0).OrderBy(t => t.Minutes).Select(FormatRow))} / 時計のずれ {seconds} 秒");
            else if (Math.Abs(skew) >= 2) log($"時計のずれ {seconds} 秒を修正");
        }
        catch (Exception ex) when (ex is DlpException or ProtocolException) { log($"HotSync の時刻の設定に失敗：{ex.Message}"); }
        return resolved;
    }
    public static string FormatRow(ScheduleTime row)
    {
        string days = string.Concat(new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday }.Zip(new[] { "月", "火", "水", "木", "金", "土", "日" }).Where(x => row.Days.Contains(x.First)).Select(x => x.Second));
        return HotSyncSchedule.Format(row.Minutes) + (row.Days.Distinct().Count() == 7 ? "" : " " + days);
    }
    public static byte[] Build(ScheduleSettings schedule)
    {
        var result = new byte[Size]; Put(result, EnabledOffset, 1);
        for (int d = 0; d < 7; d++)
        {
            int[] times = schedule.Times.Where(t => t.Days.Contains((DayOfWeek)d)).Select(t => t.Minutes).Distinct().Order().ToArray();
            if (times.Length > HotSyncSchedule.MaxTimes) throw new ArgumentException("At most 8 times per day");
            Put(result, d * 2, times.Length);
            for (int i = 0; i < times.Length; i++) Put(result, TimesOffset + (d * HotSyncSchedule.MaxTimes + i) * 2, times[i]);
        }
        return result;
    }
    public static bool Matches(byte[] current, ScheduleSettings schedule)
    {
        if (Get(current, EnabledOffset) != 1) return false;
        byte[] expected = Build(schedule);
        for (int d = 0; d < 7; d++)
        {
            int count = Get(expected, d * 2);
            if (Get(current, d * 2) != count) return false;
            for (int i = 0; i < count; i++) if (Get(current, TimesOffset + (d * HotSyncSchedule.MaxTimes + i) * 2) != Get(expected, TimesOffset + (d * HotSyncSchedule.MaxTimes + i) * 2)) return false;
        }
        return true;
    }
    public static int Skew(DateTime device, DateTime pc) => (int)Math.Round((device - pc).TotalSeconds, MidpointRounding.AwayFromZero);
    private static void Put(byte[] b, int offset, int value) { b[offset] = (byte)(value >> 8); b[offset + 1] = (byte)value; }
    private static int Get(byte[] b, int offset) => b[offset] << 8 | b[offset + 1];
}
