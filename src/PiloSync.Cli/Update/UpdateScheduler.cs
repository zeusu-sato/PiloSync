namespace PiloSync.Cli.Update;

public sealed class UpdateScheduler(
    IUpdateFetcher fetcher,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> delay = delay ?? Task.Delay;

    private int manualChecking;

    public async Task RunAsync(string? currentTag, Func<bool> enabled, Func<string?> failedTag,
        Func<StagedUpdate, CancellationToken, Task<bool>> tryInstall, Action<string> log, CancellationToken token)
    {
        if (!ReleaseSelection.IsTag(currentTag) || !enabled()) return;
        await delay(TimeSpan.FromMinutes(1), token);
        while (!token.IsCancellationRequested && enabled())
        {
            var result = await CheckAsync(currentTag!, enabled, failedTag, tryInstall, log, token);
            if (result is ManualCheckResult.Installed or ManualCheckResult.Cancelled || !enabled()) return;
            await delay(TimeSpan.FromHours(6), token);
        }
    }

    public async Task<ManualCheckResult> CheckNowAsync(string? currentTag, Func<string?> failedTag,
        Func<StagedUpdate, CancellationToken, Task<bool>> tryInstall, Action<string> log, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref manualChecking, 1, 0) != 0) return ManualCheckResult.Busy;
        try
        {
            if (!ReleaseSelection.IsTag(currentTag)) return ManualCheckResult.Failed;
            return await CheckAsync(currentTag!, () => true, failedTag, tryInstall, log, token);
        }
        finally { Interlocked.Exchange(ref manualChecking, 0); }
    }

    private async Task<ManualCheckResult> CheckAsync(string currentTag, Func<bool> enabled, Func<string?> failedTag,
        Func<StagedUpdate, CancellationToken, Task<bool>> tryInstall, Action<string> log, CancellationToken token)
    {
        StagedUpdate? staged = null;
        bool handedOver = false;
        try
        {
            token.ThrowIfCancellationRequested();
            string? failed = failedTag();
            string baseline = ReleaseSelection.IsTag(failed) && string.CompareOrdinal(failed, currentTag) > 0 ? failed! : currentTag;
            staged = await fetcher.FetchAsync(baseline, log, token);
            if (staged is null || string.CompareOrdinal(staged.Tag, baseline) <= 0) return ManualCheckResult.Latest;
            while (!token.IsCancellationRequested && enabled())
            {
                try
                {
                    if (await tryInstall(staged, token)) { handedOver = true; return ManualCheckResult.Installed; }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return ManualCheckResult.Cancelled; }
                catch (Exception ex) { log($"更新の確認: 失敗 {ex.Message}"); }
                await delay(TimeSpan.FromMinutes(1), token);
            }
            return ManualCheckResult.Cancelled;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return ManualCheckResult.Cancelled; }
        catch (Exception ex) { log($"更新の確認: 失敗 {ex.Message}"); return ManualCheckResult.Failed; }
        finally { if (staged is not null && !handedOver) Cleanup(staged); }
    }

    internal static void Cleanup(StagedUpdate staged)
    {
        string? root = Path.GetDirectoryName(Path.GetFullPath(staged.Folder));
        if (root is not null && Path.GetFileName(root).StartsWith("PiloSync-update-", StringComparison.Ordinal))
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

public enum ManualCheckResult { Latest, Installed, Failed, Busy, Cancelled }
