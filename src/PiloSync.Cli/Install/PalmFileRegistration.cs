using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace PiloSync.Cli;

public sealed class PalmFileRegistration(RegistryKey root)
{
    const string ProgId = "PiloSync.PalmFile";
    static readonly string[] Extensions = [".prc", ".pdb", ".pqa"];

    public bool IsRegistered()
    {
        using var key = root.OpenSubKey(ProgId);
        return key is not null;
    }

    public void Register(string executable)
    {
        string command = $"\"{executable}\" \"%1\"";
        using (var key = root.CreateSubKey(ProgId)) key.SetValue("FriendlyTypeName", "Palm のファイル");
        using (var key = root.CreateSubKey(ProgId + @"\DefaultIcon")) key.SetValue("", executable + ",0");
        using (var key = root.CreateSubKey(ProgId + @"\shell\open\command")) key.SetValue("", command);
        using (var key = root.CreateSubKey(@"Applications\PiloSync.exe")) key.SetValue("FriendlyAppName", "PiloSync");
        using (var key = root.CreateSubKey(@"Applications\PiloSync.exe\SupportedTypes"))
            foreach (string extension in Extensions) key.SetValue(extension, "");
        using (var key = root.CreateSubKey(@"Applications\PiloSync.exe\shell\open\command")) key.SetValue("", command);
        foreach (string extension in Extensions)
            using (var key = root.CreateSubKey(extension + @"\OpenWithProgids")) key.SetValue(ProgId, "");
    }

    public void Unregister()
    {
        foreach (string extension in Extensions)
            using (var key = root.OpenSubKey(extension + @"\OpenWithProgids", true)) key?.DeleteValue(ProgId, false);
        root.DeleteSubKeyTree(ProgId, false);
        root.DeleteSubKeyTree(@"Applications\PiloSync.exe", false);
    }

    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
    public static void NotifyChanged() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
}
