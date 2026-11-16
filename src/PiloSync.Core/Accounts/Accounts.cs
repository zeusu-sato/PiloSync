
namespace PiloSync.Core;

public enum AccountResolutionKind { IdMatch, NameMatch, Created, Unregistered, Assigned }
public sealed record AccountResolution(AccountResolutionKind Kind, PalmAccount? Account, bool WriteDeviceUserId, string? Warning = null);

public sealed class AccountStore
{
    public const string SemaphoreName = "Users.dat semaphore";
    public string DataFolder { get; }
    public UsersDat Data { get; private set; } = new();
    private byte[]? loadedBytes;
    private readonly TimeSpan timeout;
    public AccountStore(string dataFolder, TimeSpan? semaphoreTimeout = null)
    {
        DataFolder = Path.GetFullPath(dataFolder);
        timeout = semaphoreTimeout ?? TimeSpan.FromSeconds(2);
        Load();
    }
    public void Load()
    {
        string path = Path.Combine(DataFolder, "users.dat");
        loadedBytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
        Data = loadedBytes is null ? new() : UsersDat.Read(loadedBytes);
    }
    public void Save()
    {
        // Stock uses CreateMutexA despite naming the object a semaphore (Instaide 0x6705D49F).
        using var semaphore = new Mutex(false, SemaphoreName);
        bool acquired;
        try { acquired = semaphore.WaitOne(timeout); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new TimeoutException("users.dat のセマフォを取得できませんでした。");
        string? temp = null;
        try
        {
            Directory.CreateDirectory(DataFolder);
            string path = Path.Combine(DataFolder, "users.dat");
            byte[]? current = File.Exists(path) ? File.ReadAllBytes(path) : null;
            if ((current is null) != (loadedBytes is null) || (current is not null && !current.AsSpan().SequenceEqual(loadedBytes)))
                throw new IOException("users.dat が他の処理で更新されました。再読み込みしてください。");
            byte[] bytes = Data.Write();
            temp = Path.Combine(DataFolder, $".users-{Guid.NewGuid():N}.tmp");
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            loadedBytes = bytes;
        }
        finally { if (temp is not null && File.Exists(temp)) File.Delete(temp); semaphore.ReleaseMutex(); }
    }
    public string GetUserFolder(PalmAccount account)
    {
        string folder = account.Folder;
        if (string.IsNullOrWhiteSpace(folder) || folder is "." or ".." || folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.IsPathRooted(folder) || folder.EndsWith('.') || folder.EndsWith(' ') || IsReserved(folder))
            throw new InvalidDataException("不正なユーザーフォルダです。");
        string path = Path.GetFullPath(Path.Combine(DataFolder, folder));
        if (!path.StartsWith(DataFolder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("保存先の外は使えません。");
        for (var dir = new DirectoryInfo(path); dir is not null; dir = dir.Parent)
            if (dir.Exists && dir.LinkTarget is not null) throw new InvalidDataException("ユーザーフォルダにリンクは使えません。");
        return path;
    }
    public string GetBackupFolder(PalmAccount account) => CreateChild(account, "Backup");
    public string GetBackupArchiveFolder(PalmAccount account) => Path.Combine(GetUserFolder(account), "Backup Archive");
    public string GetInstallFolder(PalmAccount account) => CreateChild(account, "Install");
    private string CreateChild(PalmAccount account, string child)
    {
        string path = Path.Combine(GetUserFolder(account), child);
        if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is not null) throw new InvalidDataException("保存先にリンクは使えません。");
        Directory.CreateDirectory(path); return path;
    }
    public AccountResolution Resolve(string deviceName, uint deviceUserId)
    {
        var user = deviceUserId == 0 ? null : Data.Users.FirstOrDefault(u => u.Id == deviceUserId);
        if (user is not null) return new(AccountResolutionKind.IdMatch, user, false, user.Name == deviceName ? null : $"端末の名前が変更されています（登録名: {user.Name}、端末: {deviceName}）。");
        user = Data.Users.FirstOrDefault(u => u.Name == deviceName);
        if (user is not null) return new(AccountResolutionKind.NameMatch, user, user.Id != deviceUserId);
        if (string.IsNullOrEmpty(deviceName)) return new(AccountResolutionKind.Unregistered, null, false);
        user = Create(deviceName);
        return new(AccountResolutionKind.Created, user, true);
    }
    public AccountResolution Assign(string accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName)) throw new ArgumentException("アカウント名が必要です。", nameof(accountName));
        var user = Data.Users.FirstOrDefault(u => u.Name == accountName) ?? Create(accountName);
        return new(AccountResolutionKind.Assigned, user, true);
    }
    private PalmAccount Create(string name)
    {
        // Stock: srand(GetTickCount()), rand(), then increment until >255 and unused.
        // Reproduce the CRT's first rand value without loading the stock DLL.
        uint seed = unchecked((uint)Environment.TickCount * 0x343fdu + 0x269ec3u);
        uint id = (seed >> 16) & 0x7fff;
        while (id <= 255 || Data.Users.Any(u => u.Id == id)) id = checked(id + 1);
        string stem = new(name.Where(c => !char.IsControl(c) && !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
        stem = stem.Trim().TrimEnd('.', ' ');
        if (stem.Length == 0) stem = "Palm";
        stem = stem[..Math.Min(6, stem.Length)].TrimEnd('.', ' ');
        if (stem.Length == 0) stem = "Palm";
        if (IsReserved(stem)) stem = "_" + stem[..Math.Min(5, stem.Length)];
        string folder = stem;
        for (int suffix = 2; Data.Users.Any(u => string.Equals(u.Folder, folder, StringComparison.OrdinalIgnoreCase)) || Directory.Exists(Path.Combine(DataFolder, folder)) || File.Exists(Path.Combine(DataFolder, folder)); suffix++)
        {
            string digits = suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (digits.Length >= 6) throw new IOException("ユーザーフォルダ名を確保できません。");
            folder = stem[..Math.Min(stem.Length, 6 - digits.Length)] + digits;
        }
        var user = new PalmAccount { Id = id, Name = name, Folder = folder, Flags = 1 };
        Data.Users.Add(user);
        try { Save(); } catch { Data.Users.Remove(user); throw; }
        return user;
    }
    private static bool IsReserved(string name)
    {
        string n = name.Split('.')[0].ToUpperInvariant();
        return n is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" || (n.Length == 4 && (n.StartsWith("COM") || n.StartsWith("LPT")) && "123456789¹²³".Contains(n[3]));
    }
}

