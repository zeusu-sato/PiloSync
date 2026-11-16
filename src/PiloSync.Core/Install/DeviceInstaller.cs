using System.Diagnostics;

namespace PiloSync.Protocol;

public sealed record DeviceInstallResult(IReadOnlyList<string> Succeeded, IReadOnlyList<string> Failed, int RemainingCount, bool ResetRequired = false, IReadOnlyList<string>? Skipped = null);
public static class DeviceInstaller
{
    internal static bool Supported(string path) => new[] { ".prc", ".pdb", ".pqa" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    public static DeviceInstallResult Run(DlpClient dlp, string installDir, Action<string> log, Action<SyncProgress>? progress = null)
    {
        var succeeded = new List<string>(); var failed = new List<string>(); var skipped = new List<string>();
        List<PalmDatabase>? databases = null; bool resetRequired = false;
        if (!Directory.Exists(installDir)) return new(succeeded, failed, 0, Skipped: skipped);
        foreach (string directory in Directory.EnumerateDirectories(installDir)) log($"未対応: カード用サブフォルダー {directory}");
        var paths = Directory.EnumerateFiles(installDir).Where(Supported).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        for (int i = 0; i < paths.Length; i++)
        {
            string path = paths[i];
            progress?.Invoke(new(SyncPhase.Install, i + 1, paths.Length, Path.GetFileName(path)));
            var timer = Stopwatch.StartNew();
            long bytes = 0;
            string filename = Path.GetFileName(path); PalmDbFile file;
            // Keep the exclusive handle through transfer; another writer cannot replace the source mid-install.
            FileStream input;
            try { input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add(filename); log($"インストール保留 {filename}: {ex.Message}"); continue; }
            bool installed = false;
            using (input)
            {
                try { bytes = input.Length; file = PalmDbFile.Read(input); Validate(file); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { failed.Add(filename); log($"解析失敗 {filename}: {ex.Message}"); continue; }
                bool createAttempted = false; string stage = "ReadDBList";
                try
                {
                    // Include ROM for the explicit read-only check. Old DLP versions may not set misc RAM flags.
                    databases ??= dlp.ReadDBList(includeRom: true).ToList();
                    var existing = databases.Where(d => d.Name == file.Name).ToArray();
                    var ram = existing.Any(d => !d.IsRom);
                    if (existing.Length != 0 && !ram) { failed.Add(filename); log($"インストール保留 {filename}: ROM の DB は削除できません"); continue; }
                    stage = "ReadStorageInfo";
                    var memory = dlp.ReadStorageInfo();
                    // The file already contains the item directory (8 bytes per record or 10 per resource).
                    // Allow another 16 bytes per item for Palm OS heap chunk/handle management and alignment;
                    // this is a conservative estimate, since overhead varies by OS and heap format.
                    long required = bytes + 16L * (file.IsResource ? file.Resources.Count : file.Records.Count);
                    long available = memory?.FreeRam ?? 0;
                    bool known = memory is not null && memory.FreeRam > 0;
                    if (known && ram) { stage = "ReadDBSize"; var size = dlp.ReadDBSize(file.Name); known = size.HasValue; available += size ?? 0; }
                    if (known && required > available)
                    {
                        skipped.Add(filename);
                        log($"インストール見送り {filename}: 端末の空きが足りません（必要 約 {(required + 1023) / 1024:N0} KB / 空き {available / 1024:N0} KB）");
                        dlp.AddSyncLogEntry("PiloSync: install skipped, insufficient memory.\n");
                        continue;
                    }
                    if (ram) { stage = "DeleteDB"; dlp.DeleteDB(file.Name); databases.RemoveAll(d => d.Name == file.Name && !d.IsRom); }
                    createAttempted = true;
                    ushort attributes = (ushort)(file.Attributes & ~(0x8000 | 0x4000 | 0x0004));
                    stage = "CreateDB";
                    byte handle = dlp.CreateDB(file.Type, file.Creator, 0, attributes, file.Version, file.Name);
                    bool bodyFailed = true; bool transportFailed = false;
                    try
                    {
                        if (file.AppInfo.Length != 0) { stage = "AppBlock"; dlp.WriteAppBlock(handle, file.AppInfo); }
                        if (file.SortInfo.Length != 0) { stage = "SortBlock"; dlp.WriteSortBlock(handle, file.SortInfo); }
                        if (file.IsResource) { int n = 0; foreach (var resource in file.Resources) { stage = $"Resource {++n}/{file.Resources.Count}"; dlp.WriteResource(handle, resource); } }
                        else { int n = 0; foreach (var record in file.Records) { stage = $"Record {++n}/{file.Records.Count}"; dlp.WriteRecord(handle, record); } }
                        bodyFailed = false;
                    }
                    catch (Exception ex) when (ex is not DlpException) { transportFailed = true; throw; }
                    finally
                    {
                        try { if (!transportFailed) { if (!bodyFailed) stage = "CloseDB"; dlp.CloseDB(handle); } }
                        catch (DlpException ex) when (bodyFailed) { log($"CloseDB {file.Name}: {ex.Message}"); }
                    }
                    installed = true;
                    resetRequired |= (file.Attributes & 0x0020) != 0;
                    databases.Add(new(file.Name, file.Type, file.Creator, attributes, file.Version, file.ModificationNumber, file.Modified, 0));
                }
                catch (Exception ex) when (ex is not DlpException) { if (createAttempted) log($"途中まで書いた DB が端末に残っている可能性: {file.Name}"); log($"インストール中断 {filename}: {stage}: {ex.Message}"); throw; }
                catch (DlpException ex)
                {
                    failed.Add(filename); log($"インストール失敗 {filename}: {ex.Message}");
                    if (createAttempted)
                        try { dlp.DeleteDB(file.Name); databases!.RemoveAll(d => d.Name == file.Name && !d.IsRom); } catch (DlpException cleanup) { log($"作成途中の DB 削除失敗 {file.Name}: {cleanup.Message}"); }
                }
            }
            if (installed)
            {
                try { File.Delete(path); succeeded.Add(filename); log($"インストール成功 {filename} ({bytes:N0} バイト, {timer.Elapsed.TotalSeconds:F1} 秒)"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add(filename); log($"端末へのインストール完了、ファイル削除失敗 {filename}: {ex.Message}"); }
            }
        }
        return new(succeeded, failed, Directory.EnumerateFiles(installDir).Count(Supported), resetRequired, skipped);
    }
    internal static void Validate(PalmDbFile file)
    {
        if (PalmText.Encode(file.Name).Length is 0 or > 31) throw new InvalidDataException("Invalid DB name");
        if (file.AppInfo.Length > 65525 || file.SortInfo.Length > 65525 || file.Records.Any(r => r.Data.Length > 65521) || file.Resources.Any(r => r.Data.Length > 65519)) throw new InvalidDataException("64K を超える DLP コマンドは未対応です");
    }
}
