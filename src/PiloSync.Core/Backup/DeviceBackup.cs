using System.Diagnostics;

namespace PiloSync.Protocol;

public sealed record DeviceBackupResult(IReadOnlyList<string> Saved, IReadOnlyList<string> Skipped, IReadOnlyList<string> Failed)
{
    public IReadOnlyDictionary<string, string> ConfirmedFiles { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyList<string> InUse { get; init; } = [];
    public int SavedCount => Saved.Count;
    public int SkippedCount => Skipped.Count;
    public int FailedCount => Failed.Count;
}
public static class DeviceBackup
{
    public static DeviceBackupResult Run(DlpClient dlp, IReadOnlyList<PalmDatabase> dbs, string backupDir, Action<string> log, Action<SyncProgress>? progress = null)
    {
        Directory.CreateDirectory(backupDir);
        var saved = new List<string>(); var skipped = new List<string>(); var failed = new List<string>();
        var inUse = new List<string>();
        var confirmed = new Dictionary<string, string>(StringComparer.Ordinal);
        // A sanitized-name collision must never overwrite another database's backup.
        var collisions = dbs.Where(d => !d.IsRom).GroupBy(d => BackupFileName.For(d.Name, (d.Attributes & 1) != 0, d.Type, d.Creator), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int total = dbs.Count(d => !d.IsRom), index = 0;
        foreach (var db in dbs.Where(d => !d.IsRom))
        {
            progress?.Invoke(new(SyncPhase.Backup, ++index, total, db.Name));
            var timer = Stopwatch.StartNew();
            string name = BackupFileName.For(db.Name, (db.Attributes & 1) != 0, db.Type, db.Creator);
            if (collisions.Contains(name)) { failed.Add(db.Name); log($"バックアップ失敗 {db.Name}: ファイル名が重複します ({name})"); continue; }
            string path = Path.Combine(backupDir, name); string? temporary = null; bool deviceOperation = false;
            try
            {
                if (Unchanged(path, db, out var previous)) { skipped.Add(db.Name); confirmed[db.Name] = path; log($"変化なし {db.Name}"); continue; }
                var file = new PalmDbFile { Name = db.Name, Type = db.Type, Creator = db.Creator, Attributes = (ushort)(db.Attributes & ~0x8000), Version = db.Version, ModificationNumber = db.ModificationNumber, Created = db.Created, Modified = db.Modified, BackupDate = db.BackupDate };
                deviceOperation = true;
                byte handle = dlp.OpenDB(db.Name);
                bool bodyFailed = true;
                try
                {
                    int count = dlp.ReadOpenDBInfo(handle);
                    file.AppInfo = OptionalBlock(() => dlp.ReadAppBlock(handle)); file.SortInfo = OptionalBlock(() => dlp.ReadSortBlock(handle));
                    for (int i = 0; i < count; i++)
                        if (file.IsResource) file.Resources.Add(dlp.ReadResource(handle, (ushort)i)); else file.Records.Add(dlp.ReadRecord(handle, (ushort)i));
                    file.UniqueIdSeed = file.Records.Count == 0 ? 0 : file.Records.Max(r => r.UniqueId) + 1;
                    bodyFailed = false;
                }
                finally
                {
                    try { dlp.CloseDB(handle); }
                    catch (DlpException ex) when (bodyFailed) { log($"CloseDB {db.Name}: {ex.Message}"); }
                }
                deviceOperation = false;
                temporary = Path.Combine(backupDir, "." + Guid.NewGuid().ToString("N") + ".tmp");
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(output); output.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                temporary = null; saved.Add(db.Name); confirmed[db.Name] = path; log($"保存 {db.Name} ({new FileInfo(path).Length:N0} バイト, {timer.Elapsed.TotalSeconds:F1} 秒) / {Difference(previous, db)}");
            }
            catch (DlpException ex) { bool busy = SystemDatabase.IsInUse(db, db.Type, db.Creator, ex); if (busy) inUse.Add(db.Name); else failed.Add(db.Name); log($"バックアップ{(busy ? "使用中で読めず" : "失敗")} {db.Name}: {ex.Message} / 型 {db.Type} / 作成者 {db.Creator} / 属性 0x{db.Attributes:X4}"); }
            catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException or ArgumentException || (!deviceOperation && ex is IOException)) { failed.Add(db.Name); log($"バックアップ失敗 {db.Name}: {ex.Message}"); }
            catch (Exception ex) when (deviceOperation) { log($"バックアップ中断 {db.Name}: {ex.Message}"); throw; }
            finally { if (temporary != null) { try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log($"一時ファイル削除失敗: {ex.Message}"); } } }
        }
        return new(saved, skipped, failed) { ConfirmedFiles = confirmed, InUse = inUse };
    }
    private static byte[] OptionalBlock(Func<byte[]> read) { try { return read(); } catch (DlpException ex) when (ex.Code == 5) { return []; } }
    private static string Difference(PalmDbFile? previous, PalmDatabase db) =>
        $"修正番号 {(previous is null ? "前回なし" : previous.ModificationNumber)}→{db.ModificationNumber} / 変更日時 {(previous is null ? "前回なし" : Format(previous.Modified))}→{Format(db.Modified)}";
    private static string Format(DateTime? time) => time?.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) ?? "日時なし";
    private static bool Unchanged(string path, PalmDatabase db, out PalmDbFile? previous)
    {
        previous = null;
        if (!File.Exists(path)) return false;
        try
        {
            using var input = File.OpenRead(path); var file = previous = PalmDbFile.Read(input);
            return file.Name == db.Name && file.Type == db.Type && file.Creator == db.Creator && file.IsResource == ((db.Attributes & 1) != 0) && file.ModificationNumber == db.ModificationNumber && file.Modified == db.Modified && file.Version == db.Version;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return false; }
    }
}

internal static class SystemDatabase
{
    internal static bool IsInUse(PalmDatabase? deviceEntry, string type, string creator, DlpException ex) =>
        ex.Code is 0x01 or 0x07 or 0x0A or 0x0C &&
        ((deviceEntry is not null && (deviceEntry.Attributes & 0x8000) != 0) || creator is "psys" or "netl");
}
