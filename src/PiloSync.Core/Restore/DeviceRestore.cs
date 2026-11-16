namespace PiloSync.Protocol;

public sealed record RestoreIssue(string Name, string Reason);
public sealed record RestoreDatabase(PalmDbFile File, long Size, bool ReplaceRam)
{
    public PalmDatabase? DeviceEntry { get; init; }
    public int MaxBlock => File.IsResource ? File.Resources.Select(r => r.Data.Length).DefaultIfEmpty().Max() : File.Records.Select(r => r.Data.Length).DefaultIfEmpty().Max();
}
public sealed record DeviceRestorePlan(IReadOnlyList<RestoreDatabase> Databases, IReadOnlyList<RestoreIssue> Skipped, IReadOnlyList<RestoreIssue> Failed, long RequiredBytes, long? FreeBytes)
{
    public string? StopReason => FreeBytes is { } free && RequiredBytes > free ? $"空きメモリーが足りません：必要 約 {(RequiredBytes + 1023) / 1024:N0} KB / 空き {free / 1024:N0} KB。端末を初期化すると空きメモリーが増えます" : null;
}
public sealed record DeviceRestoreResult(IReadOnlyList<string> Succeeded, IReadOnlyList<RestoreIssue> Skipped, IReadOnlyList<RestoreIssue> Failed, long WrittenBytes, bool ResetRequired, string? StopReason = null)
{
    public IReadOnlyList<RestoreIssue> InUse { get; init; } = [];
}
public static class DeviceRestore
{
    public static DeviceRestorePlan Plan(DlpClient dlp, string backupDir, Action<string> log)
    {
        var files = new List<BackupCandidate>(); var skipped = new List<RestoreIssue>(); var failed = new List<RestoreIssue>();
        if (!Directory.Exists(backupDir)) failed.Add(new(backupDir, "バックアップフォルダーがありません"));
        else foreach (string path in BackupTidy.Paths(backupDir))
        {
            try { files.Add(BackupTidy.Read(path)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException) { failed.Add(new(Path.GetFileName(path), ex.Message)); }
        }
        var databases = dlp.ReadDBList(includeRom: true);
        var winners = files.GroupBy(f => f.File.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => BackupTidy.Select(g), StringComparer.Ordinal);
        var selected = new List<RestoreDatabase>();
        foreach (var item in files)
        {
            var file = item.File;
            if (winners[file.Name] != item) { skipped.Add(new(Path.GetFileName(item.Path), $"新しいほうを使用（{Path.GetFileName(winners[file.Name].Path)}）")); continue; }
            string? reason = databases.Any(d => d.Name == file.Name && d.IsRom) ? "同じ名前の ROM の DB があります" : (file.Attributes & 2) != 0 ? "ReadOnly 属性（ROM 由来）" : null;
            if (reason is not null) { skipped.Add(new(file.Name, reason)); continue; }
            try { DeviceInstaller.Validate(file); }
            catch (InvalidDataException ex) { failed.Add(new(file.Name, ex.Message)); continue; }
            selected.Add(new(file, item.Size, databases.Any(d => d.Name == file.Name && !d.IsRom)) { DeviceEntry = databases.FirstOrDefault(d => d.Name == file.Name && !d.IsRom) });
        }
        // Stable insertion avoids relying on a non-transitive comparer in a general sort.
        var ordered = new List<RestoreDatabase>();
        foreach (var item in selected.OrderBy(f => f.File.Name, StringComparer.Ordinal))
        {
            int i = ordered.Count; while (i > 0 && Compare(ordered[i - 1], item) > 0) i--;
            ordered.Insert(i, item);
        }
        var memory = dlp.ReadStorageInfo();
        long reclaimed = ordered.Where(f => f.ReplaceRam).Sum(f => dlp.ReadDBSize(f.File.Name) ?? f.Size);
        var plan = new DeviceRestorePlan(ordered, skipped, failed, ordered.Sum(f => f.Size) - reclaimed + 32768, memory?.FreeRam);
        foreach (var item in ordered) log($"復元予定：{item.File.Name} / 型 {item.File.Type} / 作成者 {item.File.Creator} / {item.Size:N0} バイト");
        foreach (var item in skipped) log($"復元しない {item.Name}: {item.Reason}");
        foreach (var item in failed) log($"復元失敗 {item.Name}: {item.Reason}");
        log(plan.RequiredBytes <= 0
            ? $"復元合計：{ordered.Sum(f => f.Size):N0} バイト / 必要な空き：0 バイト（置き換えで空く分で足ります）"
            : $"復元合計：{ordered.Sum(f => f.Size):N0} バイト / 必要な空き（余裕込み）：{plan.RequiredBytes:N0} バイト");
        log($"置き換えで空く見込み：{reclaimed:N0} バイト");
        if (memory is null) log("警告：空きメモリーを取得できません。復元を続けます");
        else log($"空きメモリー：{memory.FreeRam:N0} バイト");
        if (plan.StopReason is { } stop) log(stop);
        return plan;
    }
    private static int Stage(PalmDbFile file) => file.Name is "Saved Preferences" or "Unsaved Preferences" or "psysLaunchDB" ? 2 : file.Type is "libr" or "extn" or "shlb" or "ovly" ? 0 : 1;
    private static int Compare(RestoreDatabase a, RestoreDatabase b)
    {
        int stage = Stage(a.File).CompareTo(Stage(b.File)); if (stage != 0) return stage;
        if (a.File.Creator == b.File.Creator && a.File.Type != b.File.Type)
        { if (a.File.Type == "appl") return 1; if (b.File.Type == "appl") return -1; }
        int size = b.MaxBlock.CompareTo(a.MaxBlock); return size != 0 ? size : StringComparer.Ordinal.Compare(a.File.Name, b.File.Name);
    }
    public static DeviceRestoreResult Run(DlpClient dlp, DeviceRestorePlan plan, Action<string> log, Action<SyncProgress>? progress = null)
    {
        var inUse = new List<RestoreIssue>();
        var succeeded = new List<string>(); var failed = plan.Failed.ToList(); long bytes = 0; bool reset = false;
        if (plan.StopReason is { } stop) return new(succeeded, plan.Skipped, failed, 0, false, stop);
        for (int i = 0; i < plan.Databases.Count; i++)
        {
            var item = plan.Databases[i]; var file = item.File; bool createAttempted = false;
            progress?.Invoke(new(SyncPhase.Restore, i + 1, plan.Databases.Count, file.Name));
            try
            {
                byte handle = 0; bool overwrite = false;
                if (item.ReplaceRam)
                {
                    try { dlp.DeleteDB(file.Name); }
                    catch (DlpException ex) when (file.IsResource && ex.Code is 7 or 10 or 12)
                    { handle = dlp.OpenDB(file.Name, 0xc0); overwrite = true; log($"復元 {file.Name}: 使用中のリソース DB を上書きします"); }
                }
                if (!overwrite) { createAttempted = true; handle = dlp.CreateDB(file.Type, file.Creator, 0, (ushort)(file.Attributes & ~(0x8000 | 0x4000 | 0x0004)), file.Version, file.Name); }
                bool bodyFailed = true;
                try
                {
                    if (!overwrite && file.AppInfo.Length != 0) { dlp.WriteAppBlock(handle, file.AppInfo); reset = true; bytes += file.AppInfo.Length; }
                    if (!overwrite && file.SortInfo.Length != 0) { dlp.WriteSortBlock(handle, file.SortInfo); reset = true; bytes += file.SortInfo.Length; }
                    if (file.IsResource) foreach (var resource in file.Resources) { dlp.WriteResource(handle, resource); reset = true; bytes += resource.Data.Length; }
                    else foreach (var record in file.Records)
                    {
                        if ((!dlp.SupportsDeletedRecords && (record.Attributes & 0x88) != 0) || (record.Data.Length == 0 && (record.Attributes & 0x80) != 0)) continue;
                        dlp.WriteRecord(handle, record with { Attributes = (byte)(record.Attributes & ~0x20) }); reset = true; bytes += record.Data.Length;
                    }
                    bodyFailed = false;
                }
                finally { try { dlp.CloseDB(handle); } catch (DlpException ex) when (bodyFailed) { log($"CloseDB {file.Name}: {ex.Message}"); } }
                succeeded.Add(file.Name); reset = true; log($"復元成功 {file.Name}");
            }
            catch (Exception ex) when (ex is DlpException or InvalidDataException or ArgumentException)
            {
                if (ex is DlpException dlpError && SystemDatabase.IsInUse(item.DeviceEntry, file.Type, file.Creator, dlpError)) inUse.Add(new(file.Name, ex.Message));
                else failed.Add(new(file.Name, ex.Message));
                log($"復元失敗 {file.Name}: {ex.Message}");
                if (createAttempted) try { dlp.DeleteDB(file.Name); } catch (DlpException cleanup) { log($"作成途中の DB 削除失敗 {file.Name}: {cleanup.Message}"); }
            }
        }
        return new(succeeded, plan.Skipped, failed, bytes, reset) { InUse = inUse };
    }
}
