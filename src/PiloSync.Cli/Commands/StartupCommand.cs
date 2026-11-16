using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace PiloSync.Cli;
public sealed record StartupItem(string Source, string Name, string Data, int Kind = 1);
public sealed record ProtectedStartup(string Path, bool Disabled);
public interface IStartupEnvironment
{
    string Executable { get; }
    bool StockRunning { get; }
    List<StartupItem>? Load();
    void Save(List<StartupItem> items);
    void DeleteState();
    IEnumerable<StartupItem> Discover();
    IEnumerable<ProtectedStartup> ProtectedItems();
    void Disable(StartupItem item);
    void Restore(StartupItem item);
    string? GetPiloSync();
    void SetPiloSync(string? command);
}
public static class StartupCommand
{
    public static bool IsOwn(string source) => source is "PalmSync" or "PiloSync";
    static void WithSettingsLock(Action action)
    {
        using var gate = new Mutex(false, @"Local\PiloSync.StartupSettings");
        bool owned; try { owned = gate.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
        if (!owned) throw new InvalidOperationException("自動起動設定の変更中です");
        try { action(); } finally { gate.ReleaseMutex(); }
    }
    public static bool IsRegistered(string? runValue, IEnumerable<string> executables) => StartupEnvironment.CommandExecutable(runValue) is string exe && executables.Any(path => string.Equals(exe, Environment.ExpandEnvironmentVariables(path), StringComparison.OrdinalIgnoreCase));
    public static bool ShouldMigrate(string? runValue, string cli, string launcher, bool launcherExists) => launcherExists && !string.Equals(cli, launcher, StringComparison.OrdinalIgnoreCase) && IsRegistered(runValue, [cli]);
    public static void MigrateRegistration(StartupEnvironment env)
    {
        WithSettingsLock(() => { if (ShouldMigrate(env.GetPiloSync(), StartupEnvironment.CurrentExecutable, StartupEnvironment.LauncherExecutable, File.Exists(StartupEnvironment.LauncherExecutable))) env.SetPiloSync($"\"{StartupEnvironment.LauncherExecutable}\" tray"); });
    }
    public static List<ProtectedStartup> Install(IStartupEnvironment env, TextWriter output)
    {
        List<ProtectedStartup> remaining = [];
        WithSettingsLock(() => { if (env.StockRunning) output.WriteLine("純正 HotSync Manager が起動中です。通知領域のアイコンから終了してください"); remaining = InstallCore(env, output, false, env.Load()); });
        return remaining;
    }
    public static void DisableRegistration(IStartupEnvironment env) => WithSettingsLock(() => env.SetPiloSync(null));
    static List<ProtectedStartup> InstallCore(IStartupEnvironment env, TextWriter output, bool dry, List<StartupItem>? state)
    {
        void Apply(string message, Action action) { output.WriteLine((dry ? "予定：" : "") + message); if (!dry) action(); }
        if (state is null) { state = env.Discover().ToList(); Apply("元の状態を startup-state.json に保存", () => env.Save(state)); }
        foreach (var item in state.Where(x => !IsOwn(x.Source))) Apply($"純正の自動起動を無効化：{item.Name}", () => env.Disable(item));
        var protectedItems = env.ProtectedItems().ToList();
        foreach (var item in protectedItems) output.WriteLine(item.Disabled ? $"純正の自動起動はタスクマネージャーで無効になっています：{item.Path}" : $"自動では停止できません：{item.Path}。タスクマネージャーの「スタートアップ アプリ」で無効にしてください。");
        if (!state.Any(x => !IsOwn(x.Source)) && protectedItems.Count == 0) output.WriteLine("純正 HotSync Manager の自動起動は見つかりませんでした（ユーザーの Run・スタートアップフォルダ、共通スタートアップ、HKLM Run を確認）");
        string command = $"\"{env.Executable}\" tray";
        Apply($"PiloSync の自動起動を登録：{command}", () => env.SetPiloSync(command));
        output.WriteLine("純正の「常に有効」設定は変更していません。純正で設定を変更した場合は、自動起動の状態を確認してください。");
        output.WriteLine("次回ログオンから、または PiloSync.exe tray で起動します。");
        return protectedItems.Where(item => !item.Disabled).ToList();
    }
    public static int Run(bool install, string[] args, IStartupEnvironment env, TextWriter output)
    {
        try
        {
            if (args.Any(x => x != "--dry-run")) throw new ArgumentException("指定できるのは --dry-run だけです");
            bool dry = args.Contains("--dry-run");
            using var gate = new Mutex(false, @"Local\PiloSync.StartupSettings");
            bool owned; try { owned = gate.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
            if (!owned) throw new InvalidOperationException("自動起動設定の変更中です");
            try
            {
                void Apply(string message, Action action) { output.WriteLine((dry ? "予定：" : "") + message); if (!dry) action(); }
                if (env.StockRunning) output.WriteLine("純正 HotSync Manager が起動中です。通知領域のアイコンから終了してください");
                var state = env.Load();
                if (install)
                {
                    InstallCore(env, output, dry, state);
                }
                else
                {
                    if (state is null) { Apply("PiloSync の自動起動を解除", () => env.SetPiloSync(null)); output.WriteLine("戻すものはありません"); return 0; }
                    Apply("PiloSync の自動起動を解除", () => env.SetPiloSync(null));
                    foreach (var item in state.Where(x => !IsOwn(x.Source))) Apply($"元の状態を復元：{item.Name}", () => env.Restore(item));
                    Apply("復元済みの記録を削除", env.DeleteState);
                }
                return 0;
            }
            finally { gate.ReleaseMutex(); }
        }
        catch (Exception ex) { output.WriteLine($"設定に失敗しました：{ex.Message}。復元記録は保持します。"); return 1; }
    }
}
public sealed class StartupEnvironment : IStartupEnvironment
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static string Folder => PiloSync.Core.AppFolder.Current;
    static string StatePath => Path.Combine(Folder, "startup-state.json");
    public static string CurrentExecutable => Environment.ProcessPath ?? throw new InvalidOperationException("exe の場所が不明です");
    public static string LauncherExecutable => Path.Combine(Path.GetDirectoryName(CurrentExecutable)!, "PiloSync.exe");
    public string Executable => File.Exists(LauncherExecutable) ? LauncherExecutable : CurrentExecutable;
    public bool StockRunning { get { var ps = Process.GetProcessesByName("HotSync"); foreach (var p in ps) p.Dispose(); return ps.Length > 0; } }
    public List<StartupItem>? Load() => File.Exists(StatePath) ? JsonSerializer.Deserialize<List<StartupItem>>(File.ReadAllText(StatePath)) ?? throw new InvalidDataException("復元記録が空です") : null;
    public void Save(List<StartupItem> items) { Directory.CreateDirectory(Folder); File.WriteAllText(StatePath + ".tmp", JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true })); File.Move(StatePath + ".tmp", StatePath, false); }
    public void DeleteState() => File.Delete(StatePath);
    static RegistryKey User() => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32);
    public static bool IsHotSync(string command)
    {
        return Path.GetFileName(CommandExecutable(command) ?? "").Equals("HotSync.exe", StringComparison.OrdinalIgnoreCase);
    }
    public static string? CommandExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string text = Environment.ExpandEnvironmentVariables(command.Trim());
        if (text.StartsWith('"')) { int end = text.IndexOf('"', 1); return end < 0 ? null : text[1..end]; }
        int last = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return last < 0 || (text.Length > last + 4 && !char.IsWhiteSpace(text[last + 4])) ? null : text[..(last + 4)];
    }
    public string? GetPiloSync() { using var root = User(); using var key = root.OpenSubKey(RunKey); return key?.GetValue("PiloSync", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string; }
    static IEnumerable<string> Links(string folder)
    {
        if (!Directory.Exists(folder)) yield break;
        foreach (string path in Directory.EnumerateFiles(folder, "*.lnk"))
        {
            object? shell = null, link = null; string target;
            try { shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!); link = ((dynamic)shell!).CreateShortcut(path); target = ((dynamic)link).TargetPath; }
            finally { if (link is not null) Marshal.FinalReleaseComObject(link); if (shell is not null) Marshal.FinalReleaseComObject(shell); }
            if (Path.GetFileName(target).Equals("HotSync.exe", StringComparison.OrdinalIgnoreCase)) yield return path;
        }
    }
    public IEnumerable<StartupItem> Discover()
    {
        using var root = User(); using var key = root.OpenSubKey(RunKey);
        foreach (string name in key?.GetValueNames() ?? [])
            if (key!.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string command && (StartupCommand.IsOwn(name) || IsHotSync(command)))
                { if (StartupCommand.IsOwn(name)) yield return new(name, name, command, (int)key.GetValueKind(name)); else yield return Approval("ApprovedRun", name); }
        foreach (string path in Links(Environment.GetFolderPath(Environment.SpecialFolder.Startup))) yield return Approval("ApprovedFolder", Path.GetFileName(path));
    }
    static string ApprovalKey(string source) => @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\" + (source == "ApprovedRun" ? "Run" : "StartupFolder");
    static StartupItem Approval(string source, string name)
    {
        using var root = User(); using var key = root.OpenSubKey(ApprovalKey(source));
        object? value = key?.GetValue(name);
        if (value is null) return new(source, name, "", -1);
        if (value is not byte[] bytes) throw new InvalidDataException($"StartupApproved の値が REG_BINARY ではありません：{name}");
        return new(source, name, Convert.ToBase64String(bytes), (int)RegistryValueKind.Binary);
    }
    public IEnumerable<ProtectedStartup> ProtectedItems()
    {
        foreach (string path in Links(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup))) yield return new(path, ProtectedDisabled("StartupFolder", Path.GetFileName(path)));
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view); using var key = root.OpenSubKey(RunKey);
            foreach (string name in key?.GetValueNames() ?? []) if (key!.GetValue(name) is string command && IsHotSync(command)) yield return new($"HKLM ({view}) Run\\{name}", ProtectedDisabled(view == RegistryView.Registry32 && Environment.Is64BitOperatingSystem ? "Run32" : "Run", name));
        }
    }
    static bool ProtectedDisabled(string source, string name)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64); using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\" + source);
                if (key?.GetValue(name) is byte[] { Length: > 0 } bytes) return (bytes[0] & 1) != 0;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        return false;
    }
    static void Move(string from, string to)
    {
        if (!File.Exists(from)) { if (File.Exists(to)) return; throw new FileNotFoundException("ショートカットがありません", from); }
        Directory.CreateDirectory(Path.GetDirectoryName(to)!); File.Move(from, to, false);
    }
    public void Disable(StartupItem item) {
        if (item.Source.StartsWith("Approved"))
        {
            try { using var root = User(); using var key = root.CreateSubKey(ApprovalKey(item.Source)); byte[] data = new byte[12]; data[0] = 3; BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4); key.SetValue(item.Name, data, RegistryValueKind.Binary); }
            catch (Exception ex) when (item.Source == "ApprovedFolder" && ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Move(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), item.Name), Path.Combine(Folder, "StartupBackup", item.Name));
            }
            return;
        }
 if (item.Source == "Link") Move(item.Name, item.Data); else { using var root = User(); using var key = root.OpenSubKey(RunKey, true); key?.DeleteValue(item.Name, false); } }
    public void Restore(StartupItem item) {
        if (item.Source == "ApprovedFolder" && File.Exists(Path.Combine(Folder, "StartupBackup", item.Name)))
        {
            Move(Path.Combine(Folder, "StartupBackup", item.Name), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), item.Name));
            return;
        }
        if (item.Source.StartsWith("Approved")) { using var root = User(); using var key = root.CreateSubKey(ApprovalKey(item.Source)); if (item.Kind == -1) key.DeleteValue(item.Name, false); else key.SetValue(item.Name, Convert.FromBase64String(item.Data), RegistryValueKind.Binary); return; }
 if (item.Source == "Link") Move(item.Data, item.Name); else { using var root = User(); using var key = root.CreateSubKey(RunKey); key.SetValue(item.Name, item.Data, (RegistryValueKind)item.Kind); } }
    public void SetPiloSync(string? command) { using var root = User(); using var key = root.CreateSubKey(RunKey); if (command is null) { key.DeleteValue("PiloSync", false); key.DeleteValue("PalmSync", false); } else key.SetValue("PiloSync", command, RegistryValueKind.String); }
}
