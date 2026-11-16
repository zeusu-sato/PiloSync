using System.Security.Cryptography;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace PiloSync.Cli;

public sealed record MigrationRecord(string PreviousPath, string NewPath, DateTimeOffset MigratedAt);
public interface IPalmRegistry
{
    string? DataPath { get; set; }
    uint? ReadInstallFlags(uint userId);
    void WriteInstallFlags(uint userId, uint flags);
    uint? StockPcId { get; }
    uint? PcId { get; set; }
    MigrationRecord? Migration { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class PalmRegistry : IPalmRegistry
{
    private const string Desktop = @"Software\U.S. Robotics\Pilot Desktop";
    private readonly string Settings;
    public PalmRegistry() : this(@"Software\PiloSync") { }
    internal PalmRegistry(string settings) { Settings = settings; }
    // The stock auto-installer is 32-bit. Explicitly use its registry view.
    private static RegistryKey CurrentUser() => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32);
    private static object? Read(string path, string name) { using var root = CurrentUser(); using var key = root.OpenSubKey(path); return key?.GetValue(name); }
    private static void Write(string path, string name, object value, RegistryValueKind kind) { using var root = CurrentUser(); using var key = root.CreateSubKey(path); key.SetValue(name, value, kind); }
    public string? DataPath { get => Read(Desktop + @"\Core", "Path") as string; set { ArgumentException.ThrowIfNullOrWhiteSpace(value); Write(Desktop + @"\Core", "Path", value, RegistryValueKind.String); } }
    public uint? ReadInstallFlags(uint userId) => Read(Desktop + @"\HotSync Manager", "Install" + userId) is int value ? unchecked((uint)value) : null;
    public void WriteInstallFlags(uint userId, uint flags)
    {
        using var root = CurrentUser(); using var key = root.OpenSubKey(Desktop + @"\HotSync Manager", true);
        string name = "Install" + userId;
        if (key?.GetValue(name) is int) key.SetValue(name, unchecked((int)flags), RegistryValueKind.DWord);
    }
    public uint? StockPcId => Read(Desktop + @"\HotSync Manager", "PCIdentifier") is int value ? unchecked((uint)value) : null;
    public uint? PcId { get => Read(Settings, "PcId") is int value ? unchecked((uint)value) : null; set { if (value is null or 0) throw new ArgumentException("PcId は0以外です。"); Write(Settings, "PcId", unchecked((int)value.Value), RegistryValueKind.DWord); } }
    public MigrationRecord? Migration
    {
        get
        {
            string path = Settings + @"\Migration";
            return Read(path, "PreviousPath") is string previous && Read(path, "NewPath") is string next && Read(path, "MigratedAt") is string at
                ? new(previous, next, DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture)) : null;
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            string path = Settings + @"\Migration";
            Write(path, "PreviousPath", value.PreviousPath, RegistryValueKind.String);
            Write(path, "NewPath", value.NewPath, RegistryValueKind.String);
            Write(path, "MigratedAt", value.MigratedAt.ToString("O"), RegistryValueKind.String);
        }
    }
}
public static class PalmRegistryOperations
{
    public static uint GetOrCreatePcId(IPalmRegistry reg)
    {
        if (reg.StockPcId is uint stock && stock != 0) return stock;
        using var gate = new Mutex(false, PiloSync.Core.AccountStore.SemaphoreName);
        bool acquired;
        try { acquired = gate.WaitOne(TimeSpan.FromSeconds(2)); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new TimeoutException("PcId の保存ロックを取得できませんでした。");
        try
        {
            if (reg.PcId is uint existing && existing != 0) return existing;
            uint id;
            do { id = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)); } while (id == 0);
            reg.PcId = id;
            return id;
        }
        finally { gate.ReleaseMutex(); }
    }

    public static void CompleteInstall(IPalmRegistry reg, uint userId, string installFolder)
    {
        // Enumerating a missing/inaccessible folder must fail rather than clear a pending flag.
        if (Directory.EnumerateFiles(installFolder).Any(IsInstallFile)) return;
        if (reg.ReadInstallFlags(userId) is uint flags) reg.WriteInstallFlags(userId, flags & ~1u);
    }
    public static bool IsInstallFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".prc" or ".pdb" or ".pqa";
}
