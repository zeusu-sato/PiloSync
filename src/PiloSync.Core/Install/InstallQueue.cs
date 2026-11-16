using PiloSync.Protocol;

namespace PiloSync.Core;

public sealed record InstallQueueResult(PalmAccount Account, string Source, bool Added, bool Skipped, string? Error = null);
public sealed record PendingInstall(PalmAccount Account, string FileName, long Size);
public sealed record InstallCancelResult(PendingInstall Item, bool Cancelled, bool Skipped, string? Error = null);
public static class InstallQueue
{
    public static IReadOnlyList<InstallCancelResult> Cancel(AccountStore store, IEnumerable<PendingInstall> items, Action<string> recycle)
    {
        var results = new List<InstallCancelResult>();
        foreach (var item in items)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(item.FileName) || item.FileName.Contains("..") || item.FileName.IndexOfAny(['/', '\\']) >= 0 || item.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.IsPathRooted(item.FileName))
                    throw new ArgumentException("不正なファイル名です。");
                string path = Path.Combine(store.GetInstallFolder(item.Account), item.FileName);
                if (!File.Exists(path)) { results.Add(new(item, false, true)); continue; }
                recycle(path);
                results.Add(new(item, true, false));
            }
            catch (FileNotFoundException) { results.Add(new(item, false, true)); }
            catch (DirectoryNotFoundException) { results.Add(new(item, false, true)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or System.Security.SecurityException or NotSupportedException or OperationCanceledException)
            { results.Add(new(item, false, false, ex.Message)); }
        }
        return results;
    }
    public static IReadOnlyList<PendingInstall> Pending(AccountStore store, IEnumerable<PalmAccount> accounts)
    {
        var pending = new List<PendingInstall>();
        foreach (var account in accounts)
        {
            try
            {
                string folder = store.GetInstallFolder(account);
                var files = Directory.EnumerateFiles(folder).Where(DeviceInstaller.Supported)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Select(path => new PendingInstall(account, Path.GetFileName(path), new FileInfo(path).Length)).ToArray();
                pending.AddRange(files);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return pending;
    }
    public static string? Check(string path)
    {
        try
        {
            if (!new[] { ".prc", ".pdb", ".pqa" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) return "対応する拡張子は .prc / .pdb / .pqa です。";
            using var input = File.OpenRead(path);
            DeviceInstaller.Validate(PalmDbFile.Read(input));
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { return ex.Message; }
    }
    public static IReadOnlyList<InstallQueueResult> Add(AccountStore store, IEnumerable<PalmAccount> accounts, IEnumerable<string> paths, Func<PalmAccount, string, bool> overwrite)
    {
        var results = new List<InstallQueueResult>();
        var files = paths.ToArray();
        foreach (var account in accounts)
        foreach (string source in files)
        {
            string? temporary = null;
            try
            {
                string? error = Check(source);
                if (error is not null) { results.Add(new(account, source, false, false, error)); continue; }
                string folder = store.GetInstallFolder(account), target = Path.Combine(folder, Path.GetFileName(source));
                bool exists = File.Exists(target);
                if (exists && !overwrite(account, target)) { results.Add(new(account, source, false, true)); continue; }
                temporary = Path.Combine(folder, $".install-{Guid.NewGuid():N}.tmp");
                File.Copy(source, temporary);
                // Validate the actual snapshot too, in case the source changed during selection/copy.
                using (var input = File.OpenRead(temporary)) DeviceInstaller.Validate(PalmDbFile.Read(input));
                if (exists) File.Replace(temporary, target, null); else File.Move(temporary, target);
                results.Add(new(account, source, true, false));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { results.Add(new(account, source, false, false, ex.Message)); }
            finally
            {
                if (temporary is not null)
                    try { File.Delete(temporary); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { results.Add(new(account, source, false, false, $"一時ファイルを削除できません: {ex.Message}")); }
            }
        }
        return results;
    }
}
