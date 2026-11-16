using System.Buffers.Binary;
using System.Text;
namespace PiloSync.Protocol;

[Flags]
public enum UserInfoChanges : byte { UserId = 0x80, LastSyncPc = 0x40, SyncDate = 0x20, Name = 0x10, ViewerId = 0x08, All = 0xf8 }
public sealed record PalmRecord(uint UniqueId, byte Attributes, byte Category, byte[] Data);
public sealed record PalmResource(string Type, ushort Id, byte[] Data);
public sealed record PalmStorageInfo(uint RomSize, uint RamSize, uint FreeRam);

public sealed partial class DlpClient
{
    private static void Put16(byte[] b, int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(offset), value);
    private static void Put32(byte[] b, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(offset), value);
    internal static byte[] FourCC(string value)
    {
        if (value.Length != 4 || value.Any(c => c > 255)) throw new ArgumentException("FourCC must contain four bytes", nameof(value));
        return Encoding.Latin1.GetBytes(value);
    }
    private static byte[] DbName(string name)
    {
        var b = PalmText.Encode(name);
        if (b.Length is 0 or > 31 || name.Contains('\0')) throw new ArgumentException("Database name must be 1..31 CP932 bytes", nameof(name));
        return b.Concat(new byte[] { 0 }).ToArray();
    }
    public byte OpenDB(string name, byte mode = 0x90, byte card = 0) => Main(Call(0x17, new DlpArgument(0x20, new byte[] { card, mode }.Concat(DbName(name)).ToArray())), 1)[0];
    public byte CreateDB(string type, string creator, byte card, ushort attributes, ushort version, string name)
    {
        var n = DbName(name); var b = new byte[14 + n.Length]; FourCC(creator).CopyTo(b, 0); FourCC(type).CopyTo(b, 4); b[8] = card;
        Put16(b, 10, attributes); Put16(b, 12, version); n.CopyTo(b, 14);
        return Main(Call(0x18, new DlpArgument(0x20, b)), 1)[0];
    }
    public void CloseDB(byte handle) => Call(0x19, new DlpArgument(0x20, [handle]));
    public void DeleteDB(string name, byte card = 0) => Call(0x1a, new DlpArgument(0x20, new byte[] { card, 0 }.Concat(DbName(name)).ToArray()));
    public ushort ReadOpenDBInfo(byte handle) => U16(Main(Call(0x2b, new DlpArgument(0x20, [handle])), 2), 0);
    public uint? ReadDBSize(string name, byte card = 0)
    {
        if (!supportsFindDB) return null;
        // pilot-link FindDBByName: GetSize only; totalBytes is at offset 4 of argument 0x21.
        try
        {
            var b = Call(0x39, new DlpArgument(0x20, new byte[] { 0x40, card }.Concat(DbName(name)).ToArray())).FirstOrDefault(a => a.Id == 0x21)?.Data;
            return b is { Length: >= 8 } ? U32(b, 4) : null;
        }
        catch (DlpException) { return null; }
    }
    public PalmStorageInfo? ReadStorageInfo(byte card = 0)
    {
        try { var b = Main(Call(0x15, new DlpArgument(0x20, [card, 0])), 30); return new(U32(b, 16), U32(b, 20), U32(b, 24)); }
        catch (DlpException) { return null; }
    }
    public byte[] ReadAppBlock(byte handle) => ReadBlock(0x1b, handle);
    public byte[] ReadSortBlock(byte handle) => ReadBlock(0x1d, handle);
    private byte[] ReadBlock(byte command, byte handle)
    {
        using var output = new MemoryStream(); int total = -1;
        do
        {
            var request = new byte[6]; request[0] = handle; Put16(request, 2, checked((ushort)output.Length));
            Put16(request, 4, (ushort)Math.Min(32768, total < 0 ? 32768 : total - output.Length));
            var b = Main(Call(command, new DlpArgument(0x20, request)), 2);
            int size = U16(b, 0); if (total < 0) total = size;
            int count = b.Length - 2;
            if (size != total || count > total - output.Length || (count == 0 && output.Length < total)) throw new ProtocolException("DLP block size/progress");
            output.Write(b.AsSpan(2));
        } while (output.Length < total);
        return output.ToArray();
    }
    public void WriteAppBlock(byte handle, byte[] data) => WriteBlock(0x1c, handle, data);
    public void WriteSortBlock(byte handle, byte[] data) => WriteBlock(0x1e, handle, data);
    private void WriteBlock(byte command, byte handle, byte[] data)
    {
        if (data.Length > 65525) throw new InvalidDataException("DLP block exceeds 64K command limit");
        var b = new byte[4 + data.Length]; b[0] = handle; Put16(b, 2, (ushort)data.Length); data.CopyTo(b, 4); Call(command, new DlpArgument(0x20, b));
    }
    // Small reads avoid the maximum-record-size lockup noted by pilot-link.
    private byte[] ReadItem(byte command, byte argument, byte handle, ushort index, bool resource)
    {
        using var output = new MemoryStream(); byte[]? first = null; int header = 10;
        int total = -1;
        do
        {
            var request = new byte[8]; request[0] = handle; Put16(request, 2, index); Put16(request, 4, checked((ushort)output.Length));
            Put16(request, 6, (ushort)Math.Min(32768, total < 0 ? 32768 : total - output.Length));
            var b = Main(Call(command, new DlpArgument(argument, request)), header);
            int size = U16(b, resource ? 8 : 6);
            if (total < 0) { total = size; first = b[..header]; }
            if (size != total || U16(b, resource ? 6 : 4) != index || b.Length - header > total - output.Length) throw new ProtocolException("DLP item size/index");
            int count = b.Length - header;
            if (count == 0 && output.Length < total) throw new ProtocolException("DLP item read made no progress");
            output.Write(b.AsSpan(header));
        } while (output.Length < total);
        return first!.Concat(output.ToArray()).ToArray();
    }
    public PalmRecord ReadRecord(byte handle, ushort index)
    {
        var b = ReadItem(0x20, 0x21, handle, index, false); return new(U32(b, 0), b[8], b[9], b[10..]);
    }
    public uint WriteRecord(byte handle, PalmRecord record)
    {
        if (record.UniqueId > 0xffffff || record.Category > 15 || record.Data.Length > 65521) throw new InvalidDataException("Invalid DLP record or size");
        var b = new byte[8 + record.Data.Length]; b[0] = handle; b[1] = 0x80; Put32(b, 2, record.UniqueId); b[6] = record.Attributes; b[7] = record.Category; record.Data.CopyTo(b, 8);
        return U32(Main(Call(0x21, new DlpArgument(0x20, b)), 4), 0);
    }
    public PalmResource ReadResource(byte handle, ushort index)
    {
        var b = ReadItem(0x23, 0x20, handle, index, true); return new(Encoding.Latin1.GetString(b, 0, 4), U16(b, 4), b[10..]);
    }
    public void WriteResource(byte handle, PalmResource resource)
    {
        if (resource.Data.Length > 65519) throw new InvalidDataException("DLP resource exceeds 64K command limit");
        var b = new byte[10 + resource.Data.Length]; b[0] = handle; FourCC(resource.Type).CopyTo(b, 2); Put16(b, 6, resource.Id); Put16(b, 8, (ushort)resource.Data.Length); resource.Data.CopyTo(b, 10); Call(0x24, new DlpArgument(0x20, b));
    }
    public static byte[] EncodeDate(DateTime? date)
    {
        var b = new byte[8]; if (date is not { } d) return b;
        Put16(b, 0, (ushort)d.Year); b[2] = (byte)d.Month; b[3] = (byte)d.Day; b[4] = (byte)d.Hour; b[5] = (byte)d.Minute; b[6] = (byte)d.Second; return b;
    }
    public void WriteUserInfo(PalmUserInfo user, UserInfoChanges changes = UserInfoChanges.All)
    {
        // DLP writes only lastSyncDate (0x20); the handheld maintains the successful sync date itself.
        var name = PalmText.Encode(user.Name + '\0'); if (name.Length > 41 || user.Name.Contains('\0')) throw new ArgumentException("Invalid user name");
        var b = new byte[22 + name.Length]; Put32(b, 0, user.UserId); Put32(b, 4, user.ViewerId); Put32(b, 8, user.LastSyncPc);
        EncodeDate(user.LastSync).CopyTo(b, 12); b[20] = (byte)changes; b[21] = (byte)name.Length; name.CopyTo(b, 22); Call(0x11, new DlpArgument(0x20, b));
    }
}

