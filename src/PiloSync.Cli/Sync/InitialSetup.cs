using PiloSync.Core;

namespace PiloSync.Cli;

public static class InitialSetup
{
    public static void Run(IPalmRegistry registry, string localAppData, Action<string> log)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(registry.DataPath)) return;
            string target = Path.Combine(AppFolder.Resolve(localAppData), "Palm OS Desktop");
            string path = Path.Combine(target, "users.dat");
            bool ExistingUsers()
            {
                if (!File.Exists(path)) return false;
                log($"初期設定は作成しません：{target} に users.dat があり、Core\\Path がありません"); return true;
            }
            if (ExistingUsers()) return;
            using var semaphore = new Mutex(false, AccountStore.SemaphoreName);
            bool acquired;
            try { acquired = semaphore.WaitOne(TimeSpan.FromSeconds(2)); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new TimeoutException("users.dat のセマフォを取得できませんでした。");
            try
            {
                if (!string.IsNullOrWhiteSpace(registry.DataPath) || ExistingUsers()) return;
                Directory.CreateDirectory(target);
                new AccountStore(target).Save();
                try { registry.DataPath = target; }
                catch { File.Delete(path); throw; }
                log($"初期設定を作成しました：{target}");
            }
            finally { semaphore.ReleaseMutex(); }
        }
        catch (Exception ex) { log($"警告：初期設定を作成できませんでした：{ex.Message}"); }
    }
}
