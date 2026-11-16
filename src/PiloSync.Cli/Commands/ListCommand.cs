using PiloSync.Serial;
using PiloSync.Usb;
using Microsoft.Win32;
namespace PiloSync.Cli;

internal static class ListCommand
{
    public static int Run()
    {
        foreach (var p in SerialPorts.List()) Console.WriteLine($"{p.Name}  {p.FriendlyName}  CH340: {(p.IsCh340 ? "はい" : "いいえ")}");
        var devices = AceecaUsb.Enumerate();
        Console.WriteLine($"現在のUSB端末: {devices.Count} 台");
        foreach (var d in devices) Console.WriteLine($"{d.ModelName} VID_{d.Vid:X4}&PID_{d.Pid:X4}\n  {d.InstanceId}");
        Console.WriteLine("過去のUSB接続履歴（レジストリ読み取りのみ）:");
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB", false);
        int count = 0;
        foreach (var name in root?.GetSubKeyNames() ?? [])
        {
            if (!name.StartsWith("VID_0830", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("VID_082D", StringComparison.OrdinalIgnoreCase)) continue;
            using var device = root!.OpenSubKey(name, false);
            foreach (var instance in device?.GetSubKeyNames() ?? [])
            {
                using var key = device!.OpenSubKey(instance, false);
                Console.WriteLine($"  {name}\\{instance}  {key?.GetValue("FriendlyName") ?? key?.GetValue("DeviceDesc")}"); count++;
            }
        }
        Console.WriteLine($"履歴: {count} 件"); return 0;
    }
}
