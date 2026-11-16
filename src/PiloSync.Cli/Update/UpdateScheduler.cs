namespace PiloSync.Cli.Update;

public sealed class UpdateScheduler(
    IUpdateFetcher fetcher,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> delay = delay ?? Task.Delay;

    public async Task RunAsync(string? currentTag, Func<bool> enabled, Func<string?> failedTag,
        Func<StagedUpdate, CancellationToken, Task<bool>> tryInstall, Action<string> log, CancellationToken token)
    {
        if (!ReleaseSelection.IsTag(currentTag) || !enabled()) return;
        await delay(TimeSpan.FromMinutes(1), token);
        while (!token.IsCancellationRequested && enabled())
        {
            StagedUpdate? staged = null;
            bool handedOver = false;
            try
            {
                string? failed = failedTag();
                string baseline = ReleaseSelection.IsTag(failed) && string.CompareOrdinal(failed, currentTag) > 0 ? failed! : currentTag!;
                try { staged = await fetcher.FetchAsync(baseline, log, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception ex) { log($"更新の確認: 失敗 {ex.Message}"); }
                if (staged is not null && string.CompareOrdinal(staged.Tag, baseline) > 0)
                {
                    while (!token.IsCancellationRequested && enabled())
                    {
                        try
                        {
                            if (await tryInstall(staged, token)) { handedOver = true; return; }
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                        catch (Exception ex) { log($"??更新の確認: 失敗 {ex.Message}"); }
                        await delay(TimeSpan.FromMinutes(1), token);
                    }
                }
            }
            finally
            {
                if (staged is not null && !handedOver) Cleanup(staged);
            }
            if (!enabled()) return;
            await delay(TimeSpan.FromHours(6), token);
        }
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
