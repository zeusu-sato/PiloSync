using Microsoft.Win32;
using System.Diagnostics;
using System.Text.Json;
using PiloSync.Core;
namespace PiloSync.Cli;

public interface IHandoverEnvironment
{
    string LocalAppData { get; }
    bool OldRunning { get; }
    bool StockRunning { get; }
    IDisposable? LockUsers();
    void Move(string from, string to);
    string? CorePath { get; set; }
    MigrationRecord? Migration { get; set; }
    bool CopySettings();
    void RecordRename(string? previous, string? next);
    string? OldRun { get; }
    string Executable { get; }
    void SetRun(string command);
    void DeleteOldRun();
}
public sealed record HandoverResult(List<string> Actions, List<string> Warnings, bool OldRunning);
public static class RenameHandover
{
    public const string OldRunningMessage = "古い PalmSync が動いています。終了してください";
    public static HandoverResult Run(IHandoverEnvironment env)
    {
        var result = new HandoverResult([], [], env.OldRunning);
        if (result.OldRunning) { result.Warnings.Add(OldRunningMessage); return result; }
        void Try(string name, Action action) { try { action(); } catch (Exception ex) { result.Warnings.Add($"{name}に失敗しました：{ex.Message}"); } }
        Try("設定の引き継ぎ", () => { if (env.CopySettings()) result.Actions.Add("旧設定から PcId・Migration の不足分を引き継ぎました"); });
        string old = Path.Combine(env.LocalAppData, "PalmSync"), next = Path.Combine(env.LocalAppData, "PiloSync");
        bool moved = false;
        IDisposable? gate = null;
        try
        {
            if (Directory.Exists(old) && !Directory.Exists(next))
            {
                if (env.StockRunning || (gate = env.LockUsers()) is null) result.Warnings.Add("HotSync 中のため保存先を移動しませんでした");
                else { env.Move(old, next); moved = true; result.Actions.Add($"保存先を移動：{old} → {next}"); }
            }
            else if (Directory.Exists(old) && Directory.Exists(next)) result.Actions.Add("旧保存先と新保存先が両方あるため移動しませんでした");
            if (moved || (Directory.Exists(next) && !Directory.Exists(old)))
            {
                string? previous = env.CorePath, updated = ReplacePath(previous, old, next);
                if (updated != previous)
                {
                    try { env.CorePath = updated; }
                    catch
                    {
                        if (moved) Try("保存先の復元", () => { env.Move(next, old); result.Actions.Add("保存先を元に戻しました"); });
                        throw;
                    }
                    result.Actions.Add($"Core\\Path を変更：{previous} → {updated}");
                }
                if (moved || updated != previous) Try("名前変更の記録", () => env.RecordRename(previous, updated));
                Try("移行履歴の引き継ぎ", () =>
                {
                    MigrationRecord Update(MigrationRecord record) => record with { PreviousPath = ReplacePath(record.PreviousPath, old, next)!, NewPath = ReplacePath(record.NewPath, old, next)! };
                    if (env.Migration is {} record) { var changed = Update(record); if (changed != record) { env.Migration = changed; result.Actions.Add("Migration の保存先を更新しました"); } }
                    string file = Path.Combine(next, "migration.json");
                    if (File.Exists(file))
                    {
                        var recordFile = JsonSerializer.Deserialize<MigrationRecord>(File.ReadAllText(file)) ?? throw new InvalidDataException("migration.json が空です");
                        var changed = Update(recordFile);
                        if (changed != recordFile) { File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(changed)); File.Move(file + ".tmp", file, true); result.Actions.Add("migration.json の保存先を更新しました"); }
                    }
                });
            }
        }
        catch (Exception ex) { result.Warnings.Add($"保存先の引き継ぎに失敗しました：{ex.Message}"); }
        finally { gate?.Dispose(); }
        Try("自動起動の引き継ぎ", () => { if (env.OldRun is not null) { env.SetRun($"\"{env.Executable}\" tray"); env.DeleteOldRun(); result.Actions.Add("自動起動を PalmSync から PiloSync に変更"); } });
        return result;
    }
    public static bool CopyMissingSettings(IPalmRegistry source, IPalmRegistry target)
    {
        bool copied = false;
        if (target.PcId is null && source.PcId is {} id) { target.PcId = id; copied = true; }
        if (target.Migration is null && source.Migration is {} migration) { target.Migration = migration; copied = true; }
        return copied;
    }
    public static string? ReplacePath(string? path, string old, string next)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(old));
        return full.Equals(prefix, StringComparison.OrdinalIgnoreCase) || full.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase) ? next + full[prefix.Length..] : path;
    }
}
public sealed class HandoverEnvironment : IHandoverEnvironment
{
    const string Settings = @"Software\PiloSync", OldSettings = @"Software\PalmSync", RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    readonly PalmRegistry registry = new();
    static RegistryKey User() => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32);
    public string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public bool OldRunning
    {
        get
        {
            bool running = false;
            foreach (string name in new[] { "PalmSync", "palmsync-cli" }) foreach (var process in Process.GetProcessesByName(name)) using (process) { if (process.Id != Environment.ProcessId) running = true; }
            if (Mutex.TryOpenExisting(@"Local\PalmSync.Tray", out var mutex)) { mutex.Dispose(); running = true; }
            return running;
        }
    }
    public bool StockRunning => new StartupEnvironment().StockRunning;
    sealed class UsersLock(Mutex mutex) : IDisposable { public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); } }
    public IDisposable? LockUsers()
    {
        var mutex = new Mutex(false, AccountStore.SemaphoreName);
        bool owned; try { owned = mutex.WaitOne(TimeSpan.FromMilliseconds(100)); } catch (AbandonedMutexException) { owned = true; }
        if (owned) return new UsersLock(mutex); mutex.Dispose(); return null;
    }
    public void Move(string from, string to) => Directory.Move(from, to);
    public string? CorePath { get => registry.DataPath; set => registry.DataPath = value; }
    public MigrationRecord? Migration { get => registry.Migration; set => registry.Migration = value; }
    public bool CopySettings() => RenameHandover.CopyMissingSettings(new PalmRegistry(OldSettings), registry);

    public void RecordRename(string? previous, string? next)
    {
        using var root = User(); using var key = root.CreateSubKey(Settings + @"\Rename");
        key.SetValue("PreviousCorePath", previous ?? ""); key.SetValue("NewCorePath", next ?? ""); key.SetValue("RenamedAt", DateTimeOffset.UtcNow.ToString("O"));
    }
    public string? OldRun { get { using var root = User(); using var key = root.OpenSubKey(RunKey); return key?.GetValue("PalmSync") as string; } }
    public string Executable => new StartupEnvironment().Executable;
    public void SetRun(string command) { using var root = User(); using var key = root.CreateSubKey(RunKey); key.SetValue("PiloSync", command); }
    public void DeleteOldRun() { using var root = User(); using var key = root.OpenSubKey(RunKey, true); key?.DeleteValue("PalmSync", false); }
}
