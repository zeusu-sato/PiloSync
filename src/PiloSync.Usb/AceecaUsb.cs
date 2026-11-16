using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PiloSync.Usb;

public sealed record UsbDeviceInfo(string DevicePath, ushort Vid, ushort Pid, string ModelName, string InstanceId);

public static class AceecaUsb
{
    public static IReadOnlyList<UsbDeviceInfo> Enumerate()
    {
        var devices = new Dictionary<string, UsbDeviceInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var guidValue in new[] { "784126bf-4190-11d4-b5c2-00c04f687a67", "d0b3daa1-d88a-11d2-8a30-00500418785b" })
        {
            Guid guid = new(guidValue);
            IntPtr set = Native.SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, 0x12);
            if (set == new IntPtr(-1)) throw Native.Error("SetupDiGetClassDevs", Marshal.GetLastWin32Error());
            try
            {
                for (uint n = 0; ; n++)
                {
                    var data = new Native.InterfaceData { Size = Marshal.SizeOf<Native.InterfaceData>() };
                    if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, n, ref data))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == 259) break;
                        throw Native.Error("SetupDiEnumDeviceInterfaces", error);
                    }
                    var info = new Native.DevInfo { Size = Marshal.SizeOf<Native.DevInfo>() };
                    Native.SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out uint needed, ref info);
                    int detailError = Marshal.GetLastWin32Error();
                    if (detailError != 122 || needed < 6) throw Native.Error("SetupDiGetDeviceInterfaceDetail(size)", detailError);
                    IntPtr detail = Marshal.AllocHGlobal(checked((int)needed));
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!Native.SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, needed, out _, ref info))
                            throw Native.Error("SetupDiGetDeviceInterfaceDetail", Marshal.GetLastWin32Error());
                        string path = Marshal.PtrToStringUni(detail + 4)!;
                        var match = Regex.Match(path, @"VID_([0-9A-F]{4}).*PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);
                        if (!match.Success) continue;
                        ushort vid = Convert.ToUInt16(match.Groups[1].Value, 16), pid = Convert.ToUInt16(match.Groups[2].Value, 16);
                        if (vid is not (0x0830 or 0x082D)) continue;
                        var id = new StringBuilder(1024);
                        if (!Native.SetupDiGetDeviceInstanceIdW(set, ref info, id, id.Capacity, out _))
                            throw Native.Error("SetupDiGetDeviceInstanceId", Marshal.GetLastWin32Error());
                        devices.TryAdd(id.ToString(), new(path, vid, pid, GuessModel(vid, pid), id.ToString()));
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
        }
        return devices.Values.ToArray();
    }

    public static string GuessModel(ushort vid, ushort pid) => (vid, pid) switch
    {
        (0x082D, 0x0100) => "Visor Deluxe / Platinum / Prism (推定)",
        (0x0830, 0x0003) => "m515 (対象機種内の推定)",
        (0x0830, 0x0050) => "m130 (対象機種内の推定)",
        (0x0830, 0x0060) => "Tungsten C (対象機種内の推定)",
        (0x0830, 0x0061) => "Z22 (対象機種内の推定)",
        _ => "Palm / Handspring (機種未確定)"
    };
}
