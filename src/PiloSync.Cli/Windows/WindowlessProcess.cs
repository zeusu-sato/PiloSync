using System.Diagnostics;
using System.Runtime.InteropServices;
namespace PiloSync.Cli;
public static class WindowlessProcess
{
    [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
    public static void Start(string executable, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("常駐版を起動できませんでした");
    }
}
