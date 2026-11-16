namespace PiloSync.Protocol;

public sealed record BackupCandidate(string Path, PalmDbFile File, long Size, DateTime LastWriteTime);
public static class BackupTidy
{
    public static IEnumerable<string> Paths(string backupDir) => Directory.Exists(backupDir) ? Directory.EnumerateFiles(backupDir).Where(p => !Path.GetFileName(p).StartsWith('.') && new[] { ".pdb", ".prc", ".pqa" }.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.Ordinal) : [];
    public static BackupCandidate Read(string path)
    {
        using var input = File.OpenRead(path); return new(path, PalmDbFile.Read(input), input.Length, File.GetLastWriteTimeUtc(path));
    }
    public static BackupCandidate Select(IEnumerable<BackupCandidate> files) => files.OrderByDescending(f => f.File.Modified).ThenByDescending(f => f.LastWriteTime)
        .ThenByDescending(f => string.Equals(Path.GetFileName(f.Path), BackupFileName.For(f.File.Name, f.File.IsResource, f.File.Type, f.File.Creator), StringComparison.OrdinalIgnoreCase))
        .ThenBy(f => Path.GetFileName(f.Path), StringComparer.Ordinal).First();
    public static int Run(string backupDir, string archiveDir, Action<string> log, bool dryRun = false, IReadOnlyDictionary<string, string>? confirmed = null, IReadOnlySet<string>? deviceNames = null)
    {
        var files = new List<BackupCandidate>(); int count = 0;
        foreach (string path in Paths(backupDir))
        {
            try { files.Add(Read(path)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException) { }
        }
        foreach (var group in files.GroupBy(f => f.File.Name, StringComparer.Ordinal))
        {
            string? keep = confirmed is null ? Select(group).Path : confirmed.GetValueOrDefault(group.Key);
            foreach (var item in group)
            {
                string? reason = keep is not null && !string.Equals(item.Path, keep, StringComparison.OrdinalIgnoreCase) ? "古い重複" : deviceNames is not null && !deviceNames.Contains(group.Key) ? "端末にない" : null;
                if (reason is null) continue;
                string name = Path.GetFileName(item.Path);
                if (dryRun) { log($"整理予定：{name} → Backup Archive（{reason} / 残す：{Path.GetFileName(keep)}）"); count++; continue; }
                try
                {
                    Directory.CreateDirectory(archiveDir);
                    string target = Path.Combine(archiveDir, name);
                    string stem = Path.GetFileNameWithoutExtension(name) + $"-{DateTime.Now:yyyyMMdd-HHmmss}"; string extension = Path.GetExtension(name);
                    if (File.Exists(target) || Directory.Exists(target))
                    {
                        target = Path.Combine(archiveDir, stem + extension);
                        for (int i = 2; File.Exists(target) || Directory.Exists(target); i++) target = Path.Combine(archiveDir, stem + $"-{i}" + extension);
                    }
                    File.Move(item.Path, target); count++; log($"整理：{name} → Backup Archive（{reason}）");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { log($"整理失敗 {name}: {ex.Message}"); }
            }
        }
        return count;
    }
}
