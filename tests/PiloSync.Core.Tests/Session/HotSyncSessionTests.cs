using System.Buffers.Binary;
using PiloSync.Cli;
using PiloSync.Protocol;
using Xunit;

namespace PiloSync.Core.Tests;

[Collection("Accounts")]
public sealed partial class HotSyncSessionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PiloSync-session-" + Guid.NewGuid().ToString("N"));
    public HotSyncSessionTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private sealed class Device(bool registered = true) : IPacketTransport
    {
        public List<byte[]> Requests { get; } = [];
        public byte? FailCommand { get; set; }
        public string? TidyFailure { get; set; }
        public ushort RestoreError { get; set; }
        public ushort BackupError { get; set; }
        public string Creator { get; set; } = "TEST";
        public ushort ResetError { get; set; }
        public bool StorageUnavailable { get; set; }
        public bool StorageAvailable { get; set; }
        public bool StorageLow { get; set; }
        public DateTime? SuccessfulSync { get; set; }
        public DateTime? LastSync { get; set; }
        public byte[]? Preference { get; set; }
        public ushort PreferenceWriteError { get; set; }
        public TimeSpan ClockSkew { get; set; }
        private byte[] request = [];
        public void Send(ReadOnlySpan<byte> data) { request = data.ToArray(); Requests.Add(request); }
        private static byte[] Reply(byte cmd, byte[]? data = null, ushort error = 0) => data is null
            ? [(byte)(cmd | 0x80), 0, (byte)(error >> 8), (byte)error]
            : new byte[] { (byte)(cmd | 0x80), 1, 0, 0 }.Concat(DlpCodec.Request(0, new DlpArgument(0x20, data))[2..]).ToArray();
        public byte[] Receive()
        {
            byte cmd = request[0];
            if (cmd == FailCommand) throw new IOException("通信切断");
            switch (cmd)
            {
                case 0x12: return Reply(cmd, [5, 0x20, 0, 0, 0, 0, 0, 0, 0, 0]);
                case 0x10:
                    var user = new byte[registered ? 35 : 30];
                    if (registered) { BinaryPrimitives.WriteUInt32BigEndian(user, 300); user[28] = 5; "User\0"u8.CopyTo(user.AsSpan(30)); }
                    DlpClient.EncodeDate(SuccessfulSync).CopyTo(user, 12); DlpClient.EncodeDate(LastSync).CopyTo(user, 20);
                    return Reply(cmd, user);
                case 0x16:
                    // Installer reads RAM and ROM once; the next RAM listing is for backup.
                    if (TidyFailure is null && Requests.Count(r => r[0] == 0x16) <= 2) return Reply(cmd, error: 5);
                    bool rom = (request[4] & 0x40) != 0;
                    if (TidyFailure == "rom" && rom) return Reply(cmd, error: 3);
                    if (TidyFailure == "empty" && !rom) return Reply(cmd, error: 5);
                    if (TidyFailure == "page" && request[7] != 0) return Reply(cmd, error: 3);
                    if (TidyFailure == "collision" && !rom)
                    {
                        var first = DbFixtures.DatabasePage("A:B"); first[3] = 2;
                        return Reply(cmd, first.Concat(DbFixtures.DatabasePage("A?B").Skip(4)).ToArray());
                    }
                    var page = new byte[53]; page[3] = 1; page[4] = 49;
                    "DATA"u8.CopyTo(page.AsSpan(8)); System.Text.Encoding.ASCII.GetBytes(Creator).CopyTo(page, 12); "Data\0"u8.CopyTo(page.AsSpan(48));
                    if (TidyFailure == "page" && !rom) page[2] = 0x80;
                    if (TidyFailure == "rom-name" && rom) "ROM!\0"u8.CopyTo(page.AsSpan(48));
                    if (TidyFailure == "mixed" && !rom)
                    {
                        page[3] = 2;
                        return Reply(cmd, page.Concat(DbFixtures.DatabasePage("Ordinary").Skip(4)).ToArray());
                    }
                    return Reply(cmd, page);
                case 0x18: return RestoreError == 0 ? Reply(cmd, [1]) : Reply(cmd, error: RestoreError);
                case 0x1a: return Reply(cmd, error: RestoreError);
                case 0x17: if (BackupError != 0) return Reply(cmd, error: BackupError); return TidyFailure == "read" ? Reply(cmd, error: 3) : Reply(cmd, [1]);
                case 0x2b: return Reply(cmd, [0, 0]);
                case 0x1b: case 0x1d: return Reply(cmd, error: 5);
                case 0x15: { var storage = new byte[30]; if (StorageAvailable) storage[24] = 1; if (StorageLow) storage[27] = 1; return StorageUnavailable ? Reply(cmd, error: 13) : Reply(cmd, storage); }
                case 0x29: return Reply(cmd, error: ResetError);
                case 0x13: return Reply(cmd, DlpClient.EncodeDate(DateTime.Now + ClockSkew));
                case 0x34:
                    if (Preference is null) return Reply(cmd, error: 5);
                    var preference = new byte[6 + Preference.Length]; preference[1] = 1; preference[3] = (byte)Preference.Length; preference[5] = (byte)Preference.Length; Preference.CopyTo(preference, 6);
                    return Reply(cmd, preference);
                case 0x35:
                    if (PreferenceWriteError != 0) return Reply(cmd, error: PreferenceWriteError);
                    Preference = request[16..]; return Reply(cmd);
                default: return Reply(cmd);
            }
        }
    }
    private FakePalmRegistry Registry()
    {
        var store = new AccountStore(root);
        store.Data.Users.Add(new() { Id = 300, Name = "User", Folder = "User" }); store.Save();
        return new() { DataPath = root, PcId = 123 };
    }
}
