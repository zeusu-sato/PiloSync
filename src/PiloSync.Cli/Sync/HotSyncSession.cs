using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text;
using PiloSync.Core;
using PiloSync.Protocol;

namespace PiloSync.Cli;

public sealed record RestoreRequest(string AccountName, bool DryRun = false);
public static class HotSyncSession
{
    public const string UnregisteredDeviceMessage = "未登録の端末です。pilosync-cli.exe listen --assign <名前> で既存のアカウントに結びつけてください";
    private static readonly ConcurrentDictionary<string, object> AccountLocks = new(StringComparer.OrdinalIgnoreCase);
    public static bool Run(DlpClient dlp, IPalmRegistry registry, Action<string> log, Action abort, string? assign = null, Action<string>? userKnown = null, Action<bool, string>? completed = null, Action<SyncProgress>? progress = null, RestoreRequest? restore = null, string? connection = null, IReadOnlyList<int>? schedule = null)
    {
        schedule ??= HotSyncSchedule.Load(HotSyncSchedule.DefaultPath);
        bool succeeded = false, restoreWarning = false;
        string failure = "セッションを完了できませんでした";
        var timer = Stopwatch.StartNew();
        long requests = dlp.RequestCount, sent = dlp.SentBytes, received = dlp.ReceivedBytes;
        StreamWriter? file = null;
        object? accountLock = null;
        var pending = new List<string>();
        string? deviceName = connection;
        void Log(string message)
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            log(connection is null ? line : $"[{deviceName}] {line}");
            if (file is null) pending.Add(line); else file.WriteLine(line);
        }
        void Statistics()
        {
            double seconds = timer.Elapsed.TotalSeconds;
            long tx = dlp.SentBytes - sent, rx = dlp.ReceivedBytes - received;
            Log($"所要時間 {seconds:F1} 秒 / DLP 要求 {dlp.RequestCount - requests:N0} 件 / 送信 {tx:N0} バイト / 受信 {rx:N0} バイト / 平均 {(seconds > 0 ? (tx + rx) / 1024.0 / seconds : 0):F1} KB/秒");
        }
        void Stop(string message)
        {
            failure = message;
            Log(message);
            dlp.AddSyncLogEntry("PiloSync: account unavailable. Check PC log.\n");
            dlp.EndOfSync(0);
            Statistics();
            Log("終了");
        }
        try
        {
            Log("HotSync 開始");
            var sys = dlp.ReadSysInfo();
            var user = dlp.ReadUserInfo();
            if (user.Name.Length != 0) deviceName = user.Name;
            userKnown?.Invoke(user.Name);
            Log($"端末名：{user.Name} / ID={user.UserId} / Palm OS {sys.OsVersion}");
            string? path = registry.DataPath;
            if (string.IsNullOrWhiteSpace(path)) { Stop("失敗：Pilot Desktop の Core\\Path が設定されていません。"); return false; }
            if (!File.Exists(Path.Combine(path, "users.dat"))) { Stop($"失敗：users.dat がありません：{path}"); return false; }
            var store = new AccountStore(path);
            AccountResolution resolution;
            if (restore is not null)
            {
                var target = store.Data.Users.FirstOrDefault(a => a.Name == restore.AccountName);
                if (target is null) { Stop("失敗：指定したアカウントがありません"); return false; }
                resolution = new(AccountResolutionKind.Assigned, target, false);
                if (user.Name.Length != 0 && user.Name != target.Name) Log($"端末の名前 {user.Name} をアカウント {target.Name} で上書きします");
            }
            else resolution = store.Resolve(user.Name, user.UserId);
            if (resolution.Kind == AccountResolutionKind.Unregistered)
            {
                if (assign is null) { Stop(UnregisteredDeviceMessage); return false; }
                if (!store.Data.Users.Any(a => a.Name == assign)) { Stop($"失敗：指定した既存のアカウントがありません：{assign}"); return false; }
                resolution = store.Assign(assign);
            }
            var account = resolution.Account!;
            string folder = store.GetUserFolder(account);
            accountLock = AccountLocks.GetOrAdd(folder, _ => new object());
            Monitor.Enter(accountLock);
            {
                Directory.CreateDirectory(folder);
                file = new StreamWriter(Path.Combine(folder, "HotSync.log"), true, new UTF8Encoding(true)) { AutoFlush = true };
                foreach (string line in pending) file.WriteLine(line);
                pending.Clear();
                string reason = resolution.Kind switch
                {
                    AccountResolutionKind.IdMatch => "端末の ID で照合",
                    AccountResolutionKind.NameMatch => "名前で照合し ID を修正",
                    AccountResolutionKind.Created => "新しいアカウントを作成",
                    AccountResolutionKind.Assigned => "--assign で既存アカウントに登録",
                    _ => "未登録"
                };
                if (restore is not null) reason = "復元先の指定アカウント";
                Log($"アカウント：{account.Name} / {reason} / 保存先：{folder}");
                if (resolution.Warning is not null) Log(resolution.Warning);
                user = user with { Name = account.Name, UserId = account.Id };
                if (resolution.WriteDeviceUserId) dlp.WriteUserInfo(user, UserInfoChanges.UserId | UserInfoChanges.Name);
                dlp.OpenConduit();
                Action<SyncProgress>? report = progress is null ? null : p => { try { progress(p); } catch { /* Progress failure must not stop HotSync. */ } };
                if (restore is not null)
                {
                    var plan = DeviceRestore.Plan(dlp, store.GetBackupFolder(account), Log);
                    if (restore.DryRun)
                    {
                        failure = plan.StopReason ?? (plan.Failed.Count > 0 ? "復元計画に失敗があります" : "");
                        dlp.AddSyncLogEntry("PiloSync: restore dry-run; no databases written. Check PC log.\n");
                    }
                    else
                    {
                        var restored = DeviceRestore.Run(dlp, plan, Log, report);
                        Log($"復元：書き込み {restored.Succeeded.Count}、書かない {restored.Skipped.Count}、失敗 {restored.Failed.Count}（{string.Join("、", restored.Failed.Select(f => f.Name + ": " + f.Reason))}）");
                        failure = restored.StopReason ?? (restored.Failed.Count > 0 ? "復元に失敗があります" : "");
                        restoreWarning = restored.StopReason is null && restored.Failed.Count == 0 && restored.InUse.Count > 0;
                        if (restored.InUse.Count > 0) Log($"書けなかったシステム DB（使用中）：{string.Join("、", restored.InUse.Select(f => f.Name))}");
                        if (restoreWarning) failure = $"書けなかったシステム DB（使用中）：{string.Join("、", restored.InUse.Select(f => f.Name))}";
                        if (restored.StopReason is null)
                            dlp.WriteUserInfo(user with { LastSyncPc = PalmRegistryOperations.GetOrCreatePcId(registry), LastSync = DateTime.Now }, UserInfoChanges.UserId | UserInfoChanges.Name | UserInfoChanges.LastSyncPc | UserInfoChanges.SyncDate);
                        dlp.AddSyncLogEntry(restored.StopReason is not null ? "PiloSync: restore stopped: insufficient free memory.\n" : $"PiloSync: restore {restored.Succeeded.Count} OK, {restored.Skipped.Count} skipped, {restored.Failed.Count} failed, {restored.InUse.Count} in use (not written).\n");
                        if (restored.ResetRequired)
                        {
                            try { dlp.ResetSystem(); Log("端末のリセットを要求しました"); }
                            catch (Exception ex) { restoreWarning = false; failure = $"リセット失敗：{ex.Message}"; Log(failure); }
                        }
                    }
                    dlp.EndOfSync(0); Statistics(); succeeded = failure.Length == 0 || restoreWarning;
                    Log(succeeded ? restoreWarning ? "終了（正常・一部書けず）" : "終了（正常）" : "終了（失敗）"); return succeeded;
                }
                string installFolder = store.GetInstallFolder(account);
                var installed = DeviceInstaller.Run(dlp, installFolder, Log, report);
                PalmRegistryOperations.CompleteInstall(registry, account.Id, installFolder);
                ScheduleSync.Apply(dlp, schedule, Log);
                var dbs = dlp.ReadDBList();
                var backup = DeviceBackup.Run(dlp, dbs, store.GetBackupFolder(account), Log, report);
                Log($"インストール：成功 {installed.Succeeded.Count}{(installed.Skipped!.Count > 0 ? $"、見送り {installed.Skipped.Count}" : "")}、失敗 {installed.Failed.Count} / バックアップ：保存 {backup.SavedCount}、変化なし {backup.SkippedCount}{(backup.InUse.Count > 0 ? $"、使用中で読めず {backup.InUse.Count}" : "")}、失敗 {backup.FailedCount}");
                IReadOnlyList<PalmDatabase>? rom = null;
                try { rom = dlp.ReadRomDBList(); } catch (DlpException) { }
                string? tidyStop = rom is null ? "ROM 一覧が取れなかった" : backup.FailedCount > 0 ? "バックアップに失敗した DB があります" : dbs.Count == 0 ? "RAM の DB 一覧が 0 件" : null;
                if (backup.InUse.Count > 0) Log($"整理：使用中で読めなかったシステム DB（{string.Join("、", backup.InUse)}）は整理を止める理由に数えません");
                if (tidyStop is not null) Log($"整理：端末にない DB の整理は行いません（{tidyStop}）");
                try
                {
                    BackupTidy.Run(store.GetBackupFolder(account), store.GetBackupArchiveFolder(account), Log, confirmed: backup.ConfirmedFiles,
                        deviceNames: tidyStop is null ? dbs.Concat(rom!).Select(d => d.Name).ToHashSet(StringComparer.Ordinal) : null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log($"整理失敗：{ex.Message}"); }
                var memory = dlp.ReadStorageInfo();
                if (memory is not null) Log($"空きメモリー：{memory.FreeRam:N0} バイト");
                dlp.WriteUserInfo(user with { LastSyncPc = PalmRegistryOperations.GetOrCreatePcId(registry), LastSync = DateTime.Now }, UserInfoChanges.UserId | UserInfoChanges.Name | UserInfoChanges.LastSyncPc | UserInfoChanges.SyncDate);
                dlp.AddSyncLogEntry($"PiloSync: install {installed.Succeeded.Count} OK{(installed.Skipped!.Count > 0 ? $", {installed.Skipped.Count} skipped (no space)" : "")}, {installed.Failed.Count} failed; backup {backup.SavedCount} saved, {backup.SkippedCount} unchanged{(backup.InUse.Count > 0 ? $", {backup.InUse.Count} skipped (in use)" : "")}, {backup.FailedCount} failed.\n");
                if (installed.ResetRequired)
                {
                    try { dlp.ResetSystem(); Log("端末のリセットを要求しました"); }
                    catch (Exception ex) { Log($"リセット失敗：{ex.Message}"); }
                }
                dlp.EndOfSync(0);
                Statistics();
                failure = installed.Failed.Count > 0 || backup.FailedCount > 0 ? "インストールまたはバックアップに失敗があります" : "";
                Log("終了（正常）");
                file.Dispose(); file = null;
            }
            succeeded = true;
            return true;
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            try { Log($"失敗：{ex.Message}"); } catch { /* Logging failure must not stop the listener. */ }
            try { abort(); } catch { }
            try { Statistics(); Log("終了（失敗）"); } catch { }
            return false;
        }
        finally
        {
            try { completed?.Invoke(succeeded && (failure.Length == 0 || restoreWarning), failure); } catch { }
            try { file?.Dispose(); } catch { }
            if (accountLock is not null) Monitor.Exit(accountLock);
        }
    }
}
