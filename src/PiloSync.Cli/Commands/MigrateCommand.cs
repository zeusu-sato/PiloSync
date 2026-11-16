using System.Text.Json;
using PiloSync.Core;

namespace PiloSync.Cli;

public static class MigrateCommand
{
    private sealed record Candidate(string Path, UsersDat Data, DateTime Updated);
    private sealed record CopyItem(string Source, string Relative, long Size);
    public static int Run(string[] args, IPalmRegistry reg, TextWriter output) => Run(args, reg, output, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    // The explicit base directory also lets tests keep migration.json in their temporary folder.
    public static int Run(string[] args, IPalmRegistry reg, TextWriter output, string localAppData)
    {
        bool dry = false, undo = false;
        string? destination = null;
        int start = args.Length > 0 && args[0] == "migrate" ? 1 : 0;
        for (int i = start; i < args.Length; i++)
        {
            if (args[i] == "--dry-run") dry = true;
            else if (args[i] == "--undo") undo = true;
            else if (args[i] == "--to" && i + 1 < args.Length && destination is null) destination = args[++i];
            else { output.WriteLine("使い方: migrate [--dry-run] [--to <フォルダ>] または migrate --undo [--dry-run]"); return 1; }
        }
        if (undo && destination is not null) { output.WriteLine("--undo と --to は同時に使えません。"); return 1; }
        try
        {
            using var gate = new Mutex(false, AccountStore.SemaphoreName);
            bool acquired;
            try { acquired = gate.WaitOne(TimeSpan.FromSeconds(2)); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new TimeoutException("users.dat のセマフォを取得できませんでした。");
            try
            {
                string recordPath = Path.Combine(AppFolder.Resolve(localAppData), "migration.json");
                if (undo) return Undo(reg, output, dry, recordPath);
                string source = Path.GetFullPath(reg.DataPath ?? throw new IOException("Core\\Path が登録されていません。"));
                string target = Path.GetFullPath(destination ?? Path.Combine(AppFolder.Resolve(localAppData), "Palm OS Desktop"));
                CheckRoots(source, target);
                if (File.Exists(Path.Combine(target, "users.dat"))) throw new IOException("保存先に users.dat があるため、移行済みとして中止します。");
                if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
                RejectLink(source);
                if (Directory.Exists(target)) _ = Inventory(target);
                var (data, reason) = SelectUsers(source, output);
                var items = Inventory(source);
                output.WriteLine($"コピー元: {source}\nコピー先: {target}\nusers.dat の選択: {reason}");
                foreach (string directory in Directory.EnumerateDirectories(source)) output.WriteLine($"フォルダ: {Path.GetFileName(directory)}");
                long total = items.Sum(f => f.Size) + data.Write().LongLength;
                output.WriteLine($"コピー対象: {items.Count + 1} ファイル、{total} バイト（選択した users.dat を含む）");
                foreach (var user in data.Users) output.WriteLine($"ユーザー: {user.Name} / ID={user.Id} / フォルダ={user.Folder}");
                output.WriteLine($"レジストリ Core\\Path: {source} → {target}");
                output.WriteLine($"移行履歴: PreviousPath={source} / NewPath={target} / MigratedAt=移行時刻\n履歴ファイル: {recordPath}");
                if (dry) { output.WriteLine("確認のみです。変更していません。"); return 0; }
                Directory.CreateDirectory(target);
                // Keep empty Backup/Install directories as well.
                foreach (string dir in AllDirectories(source)) Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
                var failures = new List<string>();
                foreach (var item in items)
                {
                    try
                    {
                        string path = Path.Combine(target, item.Relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        File.Copy(item.Source, path, false);
                        if (new FileInfo(path).Length != item.Size || new FileInfo(item.Source).Length != item.Size) throw new IOException("サイズが一致しません。");
                    }
                    catch (Exception e) when (IsFileError(e)) { failures.Add($"{item.Source}: {e.Message}"); }
                }
                foreach (string failure in failures) output.WriteLine($"コピー失敗: {failure}");
                if (failures.Count != 0) { output.WriteLine("移行を中止しました。Core\\Path は変更していません。"); return 1; }
                var after = Inventory(source);
                if (!items.OrderBy(x => x.Relative).SequenceEqual(after.OrderBy(x => x.Relative))) throw new IOException("コピー中にコピー元のファイル一覧が変わりました。");
                // AccountStore writes via a flushed temporary file, under the same named mutex.
                var targetStore = new AccountStore(target);
                foreach (var user in data.Users) targetStore.Data.Users.Add(user);
                targetStore.Save();
                var copied = Inventory(target);
                long usersLength = new FileInfo(Path.Combine(target, "users.dat")).Length;
                if (copied.Count != items.Count || copied.Sum(x => x.Size) + usersLength != total) throw new IOException("コピー後のファイル数またはサイズが一致しません。");
                var record = new MigrationRecord(source, target, DateTimeOffset.UtcNow);
                Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);
                string temp = recordPath + ".tmp";
                try
                {
                    using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(file, record); file.Flush(true); }
                    File.Move(temp, recordPath, true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                reg.Migration = record;
                reg.DataPath = target;
                output.WriteLine("移行しました。元のデータは残しています。");
                return 0;
            }
            finally { gate.ReleaseMutex(); }
        }
        catch (Exception e) when (IsFileError(e) || e is ArgumentException or TimeoutException or JsonException or FormatException)
        { output.WriteLine($"移行を中止しました: {e.Message}"); return 1; }
    }
    private static bool IsFileError(Exception e) => e is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidDataException or OverflowException or System.Text.DecoderFallbackException;
    private static void CheckRoots(string source, string target)
    {
        string s = Path.TrimEndingDirectorySeparator(source), t = Path.TrimEndingDirectorySeparator(target);
        if (s.Equals(t, StringComparison.OrdinalIgnoreCase) || t.StartsWith(s + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || s.StartsWith(t + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("コピー元とコピー先は、互いに含まれない別のフォルダを指定してください。");
        // Never follow junctions/symbolic links outside the selected data tree.
        foreach (string root in new[] { source, target })
            for (var dir = new DirectoryInfo(root); dir is not null; dir = dir.Parent) if (dir.Exists) RejectLink(dir.FullName);
    }
    private static void RejectLink(string path)
    {
        // Cloud placeholders use ReparsePoint too; only symlinks/junctions have LinkTarget.
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (info.LinkTarget is not null) throw new IOException($"リンク先へのコピーは行えません: {path}");
    }
    private static IEnumerable<string> AllDirectories(string root)
    {
        foreach (string dir in Directory.EnumerateDirectories(root))
        { RejectLink(dir); yield return dir; foreach (string child in AllDirectories(dir)) yield return child; }
    }
    private static bool IsUsersFile(string path)
    {
        string name = Path.GetFileName(path);
        return name.Equals("users.dat", StringComparison.OrdinalIgnoreCase) || (name.StartsWith("users-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase));
    }
    private static List<CopyItem> Inventory(string root)
    {
        var result = new List<CopyItem>();
        foreach (string directory in new[] { root }.Concat(AllDirectories(root)))
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                if (directory == root && IsUsersFile(file)) continue;
                RejectLink(file);
                result.Add(new(file, Path.GetRelativePath(root, file), new FileInfo(file).Length));
            }
        return result;
    }
    private static (UsersDat Data, string Reason) SelectUsers(string root, TextWriter output)
    {
        var candidates = new List<Candidate>();
        var unreadable = new List<string>();
        foreach (string path in Directory.EnumerateFiles(root).Where(IsUsersFile).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            byte[] bytes;
            try { RejectLink(path); bytes = File.ReadAllBytes(path); }
            catch (Exception e) when (IsFileError(e)) { unreadable.Add(path); output.WriteLine($"読み取り失敗: {path}: {e.Message}"); continue; }
            try { candidates.Add(new(path, UsersDat.Read(bytes), File.GetLastWriteTimeUtc(path))); }
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException or OverflowException or System.Text.DecoderFallbackException) { output.WriteLine($"users.dat 候補を除外: {path}: {e.Message}"); }
        }
        if (unreadable.Count != 0) throw new IOException("読み取れない users.dat 候補があるため、保存先を変更しません。");
        if (candidates.Count == 0) throw new InvalidDataException("解析できる users.dat がありません。");
        var all = candidates.SelectMany(c => c.Data.Users.Select(u => u.Id)).ToHashSet();
        var containing = candidates.Where(c => all.IsSubsetOf(c.Data.Users.Select(u => u.Id))).OrderByDescending(c => c.Updated).FirstOrDefault();
        if (containing is not null) return (containing.Data, $"{Path.GetFileName(containing.Path)}（全候補の ID を含むもののうち更新日時が最新）");
        var ordered = candidates.OrderByDescending(c => c.Updated).ToList();
        var merged = ordered[0].Data;
        var ids = merged.Users.Select(u => u.Id).ToHashSet();
        foreach (var candidate in ordered.Skip(1)) foreach (var user in candidate.Data.Users) if (ids.Add(user.Id)) merged.Users.Add(user);
        return (merged, $"{Path.GetFileName(ordered[0].Path)} を元に、ほかにしかない ID を追加（和集合）");
    }
    private static int Undo(IPalmRegistry reg, TextWriter output, bool dry, string recordPath)
    {
        var record = reg.Migration ?? (File.Exists(recordPath) ? JsonSerializer.Deserialize<MigrationRecord>(File.ReadAllText(recordPath)) : null)
            ?? throw new IOException("移行履歴がありません。");
        string previous = Path.GetFullPath(record.PreviousPath), next = Path.GetFullPath(record.NewPath);
        CheckRoots(previous, next);
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(reg.DataPath ?? "")), Path.TrimEndingDirectorySeparator(next), StringComparison.OrdinalIgnoreCase))
            throw new IOException("現在の Core\\Path が移行先と一致しません。");
        var oldStore = new AccountStore(previous);
        var newStore = new AccountStore(next);
        var pending = new List<(string Source, string Target)>();
        foreach (var user in newStore.Data.Users)
        {
            string install = Path.Combine(newStore.GetUserFolder(user), "Install");
            if (!Directory.Exists(install)) continue;
            RejectLink(install);
            var original = oldStore.Data.Users.FirstOrDefault(u => u.Id == user.Id);
            string oldInstall = Path.Combine(oldStore.GetUserFolder(original ?? user), "Install");
            for (var dir = new DirectoryInfo(oldInstall); dir is not null && dir.FullName.Length >= previous.Length; dir = dir.Parent) if (dir.Exists) RejectLink(dir.FullName);
            foreach (string file in Directory.EnumerateFiles(install).Where(PalmRegistryOperations.IsInstallFile))
            {
                RejectLink(file);
                string target = Path.Combine(oldInstall, Path.GetFileName(file));
                if (!File.Exists(target)) pending.Add((file, target));
            }
        }
        output.WriteLine($"レジストリ Core\\Path: {reg.DataPath} → {previous}");
        foreach (var file in pending) output.WriteLine($"未インストールをコピー: {file.Source} → {file.Target}");
        if (dry) { output.WriteLine("確認のみです。変更していません。"); return 0; }
        var failures = new List<string>();
        foreach (var file in pending)
        {
            string? temp = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file.Target)!);
                temp = file.Target + $".{Guid.NewGuid():N}.tmp";
                File.Copy(file.Source, temp);
                if (new FileInfo(temp).Length != new FileInfo(file.Source).Length) throw new IOException("サイズが一致しません。");
                File.Move(temp, file.Target);
            }
            catch (Exception e) when (IsFileError(e)) { failures.Add($"{file.Source}: {e.Message}"); }
            finally { if (temp is not null && File.Exists(temp)) File.Delete(temp); }
        }
        foreach (string failure in failures) output.WriteLine($"コピー失敗: {failure}");
        if (failures.Count != 0) return 1;
        reg.DataPath = previous;
        output.WriteLine("元の保存先に戻しました。移行先のデータは残しています。");
        return 0;
    }
}
