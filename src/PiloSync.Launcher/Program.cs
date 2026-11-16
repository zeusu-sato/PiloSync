using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
    private static int Main(string[] args)
    {
        bool install = args.Length > 0 && args.All(File.Exists);
        if (args.Length > 0 && args[0] != "tray" && !install) { MessageBoxW(IntPtr.Zero, "コマンドは pilosync-cli.exe で実行してください（例：pilosync-cli.exe help）", "PiloSync", 0); return 0; }
        try
        {
            string executable = Path.Combine(AppContext.BaseDirectory, "pilosync-cli.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("同じフォルダに pilosync-cli.exe が見つかりません。", executable);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            start.EnvironmentVariables["PILOSYNC_LAUNCHER_VERSION"] = typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).Cast<System.Reflection.AssemblyInformationalVersionAttribute>().First().InformationalVersion;
            start.Arguments = string.Join(" ", (install ? new[] { "tray", "--install" }.Concat(args.Select(Path.GetFullPath)) : args.Length == 0 ? new[] { "tray" } : args).Select(Quote));
            using var process = Process.Start(start) ?? throw new InvalidOperationException("常駐版を起動できませんでした。");
            return 0;
        }
        catch (Exception ex) { MessageBoxW(IntPtr.Zero, ex.Message, "PiloSync", 0x10); return 1; }
    }
    private static string Quote(string argument)
    {
        var text = new StringBuilder("\""); int slashes = 0;
        foreach (char ch in argument)
        {
            if (ch == '\\') { slashes++; continue; }
            if (ch == '"') text.Append('\\', slashes * 2 + 1); else text.Append('\\', slashes);
            text.Append(ch); slashes = 0;
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }
}
