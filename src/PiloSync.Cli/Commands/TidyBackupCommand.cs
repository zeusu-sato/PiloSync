using System.Text;
using PiloSync.Core;
using PiloSync.Protocol;

namespace PiloSync.Cli;
public static class TidyBackupCommand
{
    public static int Run(string[] args, IPalmRegistry registry, TextWriter output, Func<bool>? isRunning = null)
    {
        var options = CommandLine.ParseTidyBackup(args);
        using var file = options.LogPath == null ? null : new StreamWriter(options.LogPath, true, new UTF8Encoding(false));
        void Log(string line) { output.WriteLine(line); file?.WriteLine(line); file?.Flush(); }
        if (!options.DryRun && (isRunning ?? TrayHost.IsRunning)()) { Log("常駐の PiloSync が動いています。トレイから終了してから実行してください（--dry-run は常駐中でも使えます）"); return 1; }
        if (string.IsNullOrWhiteSpace(registry.DataPath) || !File.Exists(Path.Combine(registry.DataPath, "users.dat"))) throw new ArgumentException("Core\\Path または users.dat がありません");
        var store = new AccountStore(registry.DataPath);
        var account = store.Data.Users.FirstOrDefault(a => a.Name == options.AccountName) ?? throw new ArgumentException("失敗：指定したアカウントがありません");
        int count = BackupTidy.Run(Path.Combine(store.GetUserFolder(account), "Backup"), store.GetBackupArchiveFolder(account), Log, options.DryRun);
        Log($"整理{(options.DryRun ? "予定" : "完了")}：{count} 件"); return 0;
    }
}
