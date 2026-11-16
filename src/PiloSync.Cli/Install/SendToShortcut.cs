using System.Runtime.InteropServices;

namespace PiloSync.Cli;

public static class SendToShortcut
{
    public static (string Target, string Shortcut) Paths(string executableFolder, string sendToFolder) =>
        (Path.Combine(executableFolder, "PiloSync.exe"), Path.Combine(sendToFolder, "PiloSync（Palm にインストール）.lnk"));
    public static string Create()
    {
        var (target, shortcut) = Paths(AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.SendTo));
        if (!File.Exists(target)) throw new FileNotFoundException("同じフォルダに起動用の PiloSync.exe が見つかりません。", target);
        object? shell = null, link = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell を利用できません。"));
            dynamic automation = shell!; link = automation.CreateShortcut(shortcut); dynamic item = link;
            item.TargetPath = target; item.WorkingDirectory = Path.GetDirectoryName(target); item.IconLocation = target + ",0"; item.Description = "Palm にインストール"; item.Save();
            return shortcut;
        }
        finally { if (link is not null) Marshal.FinalReleaseComObject(link); if (shell is not null) Marshal.FinalReleaseComObject(shell); }
    }
}
