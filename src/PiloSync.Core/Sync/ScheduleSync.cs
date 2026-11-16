using PiloSync.Core;
namespace PiloSync.Protocol;

public static class ScheduleSync
{
    public const string Creator = "PSas";
    public const ushort Version = 1;
    public const int Size = 128;
    private const int Days = 7, TimesOffset = 14, EnabledOffset = 126;
    public static void Apply(DlpClient dlp, IReadOnlyList<int> minutes, Action<string> log)
    {
        if (minutes.Count == 0) return;
        try
        {
            var preference = dlp.ReadAppPreference(Creator, 0, maxBytes: Size);
            if (preference is null) return;
            if (preference.Version != Version || preference.ActualSize != Size || preference.Data.Length != Size)
            {
                log($"HotSync の時刻を設定しませんでした：PSas の版 {preference.Version}、サイズ {preference.ActualSize} は未対応です");
                return;
            }
            bool write = !Matches(preference.Data, minutes);
            if (write) dlp.WriteAppPreference(Creator, 0, Version, Build(preference.Data, minutes));
            var device = dlp.ReadSysDateTime();
            var pc = DateTime.Now;
            dlp.SetSysDateTime(pc);
            string? line = Report(write, minutes, Skew(device, pc));
            if (line is not null) log(line);
        }
        catch (Exception ex) when (ex is DlpException or ProtocolException) { log($"HotSync の時刻の設定に失敗：{ex.Message}"); }
    }
    public static byte[] Build(byte[] current, IReadOnlyList<int> minutes)
    {
        if (minutes.Count > HotSyncSchedule.MaxTimes) throw new ArgumentException($"At most {HotSyncSchedule.MaxTimes} times", nameof(minutes));
        var b = new byte[Size]; current.AsSpan(EnabledOffset, 2).CopyTo(b.AsSpan(EnabledOffset));
        for (int day = 0; day < Days; day++)
        {
            Put(b, day * 2, minutes.Count);
            for (int i = 0; i < minutes.Count; i++) Put(b, TimesOffset + (day * HotSyncSchedule.MaxTimes + i) * 2, minutes[i]);
        }
        return b;
    }
    public static bool Matches(byte[] current, IReadOnlyList<int> minutes)
    {
        for (int day = 0; day < Days; day++)
        {
            if (Get(current, day * 2) != minutes.Count) return false;
            for (int i = 0; i < minutes.Count; i++)
                if (Get(current, TimesOffset + (day * HotSyncSchedule.MaxTimes + i) * 2) != minutes[i]) return false;
        }
        return true;
    }
    public static int Skew(DateTime device, DateTime pc) => (int)Math.Round((device - pc).TotalSeconds, MidpointRounding.AwayFromZero);
    public static string? Report(bool written, IReadOnlyList<int> minutes, int skew)
    {
        string seconds = skew.ToString("+0;-0;+0");
        if (written) return $"HotSync の時刻を設定 {string.Join(", ", minutes.Select(HotSyncSchedule.Format))} / 時計のずれ {seconds} 秒";
        return Math.Abs(skew) >= 2 ? $"時計のずれ {seconds} 秒を修正" : null;
    }
    private static void Put(byte[] b, int offset, int value) { b[offset] = (byte)(value >> 8); b[offset + 1] = (byte)value; }
    private static int Get(byte[] b, int offset) => b[offset] << 8 | b[offset + 1];
}
