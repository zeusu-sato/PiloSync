using System.Buffers.Binary;
using System.Text;
namespace PiloSync.Protocol;

public sealed class DlpException(ushort code) : IOException($"DLP {ErrorName(code)} (0x{code:X4})")
{
    public ushort Code { get; } = code;
    public static string ErrorName(ushort code) => code < Names.Length ? Names[code] : "dlpErrUnknown";
    private static readonly string[] Names = ["dlpErrNoError", "dlpErrSystem", "dlpErrIllegalReq", "dlpErrMemory", "dlpErrParam", "dlpErrNotFound", "dlpErrNoneOpen", "dlpErrAlreadyOpen", "dlpErrTooManyOpen", "dlpErrExists", "dlpErrOpen", "dlpErrDeleted", "dlpErrBusy", "dlpErrNotSupp", "dlpErrUnused1", "dlpErrReadOnly", "dlpErrSpace", "dlpErrLimit", "dlpErrSync", "dlpErrWrapper", "dlpErrArgument", "dlpErrSize"];
}
public sealed record DlpArgument(byte Id, byte[] Data);
public static class DlpCodec
{
    public static byte[] Request(byte command, params DlpArgument[] args)
    {
        using var stream = new MemoryStream(); stream.WriteByte(command); stream.WriteByte(checked((byte)args.Length));
        foreach (var a in args)
        {
            if (a.Data.Length <= 255) { stream.WriteByte(a.Id); stream.WriteByte((byte)a.Data.Length); }
            else if (a.Data.Length <= 65535) { stream.WriteByte((byte)(a.Id | 0x80)); stream.WriteByte(0); Span<byte> n = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(n, (ushort)a.Data.Length); stream.Write(n); }
            else { stream.WriteByte((byte)(a.Id | 0x40)); stream.WriteByte(0); Span<byte> n = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(n, (uint)a.Data.Length); stream.Write(n); }
            stream.Write(a.Data);
        }
        return stream.ToArray();
    }
    public static IReadOnlyList<DlpArgument> ParseResponse(byte command, ReadOnlySpan<byte> b)
    {
        if (b.Length < 4 || b[0] != (command | 0x80)) throw new ProtocolException("DLP response command/header");
        ushort error = BinaryPrimitives.ReadUInt16BigEndian(b[2..]); if (error != 0) throw new DlpException(error);
        var args = new List<DlpArgument>(); int offset = 4;
        for (int i = 0; i < b[1]; i++)
        {
            if (offset + 2 > b.Length) throw new ProtocolException("DLP argument header");
            byte id = b[offset]; int wrapper = (id & 0xc0) == 0 ? 2 : (id & 0xc0) == 0x80 ? 4 : (id & 0xc0) == 0x40 ? 6 : 0;
            if (wrapper == 0 || offset + wrapper > b.Length) throw new ProtocolException("DLP argument wrapper");
            uint size = wrapper == 2 ? b[offset + 1] : wrapper == 4 ? BinaryPrimitives.ReadUInt16BigEndian(b[(offset + 2)..]) : BinaryPrimitives.ReadUInt32BigEndian(b[(offset + 2)..]);
            offset += wrapper; if (size > b.Length - offset) throw new ProtocolException("DLP argument size");
            args.Add(new((byte)(id & 0x3f), b.Slice(offset, (int)size).ToArray())); offset += (int)size;
        }
        if (offset != b.Length) throw new ProtocolException("DLP trailing bytes"); return args;
    }
}
public sealed record PalmSystemInfo(uint RomVersion, uint Locale, byte[] ProductId)
{
    public string OsVersion => $"{(RomVersion >> 24) & 255}.{(RomVersion >> 20) & 15}.{(RomVersion >> 16) & 15}";
}
public sealed record PalmUserInfo(string Name, uint UserId, uint ViewerId, uint LastSyncPc, DateTime? SuccessfulSync, DateTime? LastSync);
public sealed record PalmDatabase(string Name, string Type, string Creator, ushort Attributes, ushort Version, uint ModificationNumber, DateTime? Modified, ushort Index, byte MiscFlags = 0, DateTime? Created = null, DateTime? BackupDate = null, bool IsRom = false);
public sealed record PalmAppPreference(ushort Version, ushort ActualSize, byte[] Data);
public sealed partial class DlpClient(IPacketTransport transport)
{
    private bool multiple;
    private bool supportsFindDB;
    public bool SupportsDeletedRecords { get; private set; }
    // DLP packets only (including headers and error replies); transport framing/retries are excluded.
    // Requests count Send attempts; bytes count completed Send/Receive calls.
    public long RequestCount { get; private set; }
    public long SentBytes { get; private set; }
    public long ReceivedBytes { get; private set; }
    private IReadOnlyList<DlpArgument> Call(byte command, params DlpArgument[] args)
    {
        var request = DlpCodec.Request(command, args);
        RequestCount++;
        transport.Send(request);
        SentBytes += request.Length;
        var response = command is 0x18 or 0x19 or 0x1a ? transport.Receive(60000) : transport.Receive();
        ReceivedBytes += response.Length;
        if (transport is PadpTransport padp) padp.AdvanceTransaction();
        return DlpCodec.ParseResponse(command, response);
    }
    private static byte[] Main(IReadOnlyList<DlpArgument> args, int min)
    {
        var b = args.FirstOrDefault(a => a.Id == 0x20)?.Data;
        if (b == null || b.Length < min) throw new ProtocolException("DLP missing/short primary argument"); return b;
    }
    public PalmSystemInfo ReadSysInfo()
    {
        var args = Call(0x12, new DlpArgument(0x20, [0, 1, 0, 2]));
        var version = args.FirstOrDefault(a => a.Id == 0x21)?.Data;
        SupportsDeletedRecords = version is { Length: >= 4 } && (U16(version, 0) > 1 || (U16(version, 0) == 1 && U16(version, 2) >= 1));
        multiple = version is { Length: >= 4 } && (U16(version, 0) > 1 || (U16(version, 0) == 1 && U16(version, 2) >= 2));
        supportsFindDB = multiple;
        var b = Main(args, 10);
        if (b.Length < 10 + b[9]) throw new ProtocolException("DLP product ID length");
        return new(U32(b, 0), U32(b, 4), b.AsSpan(10, b[9]).ToArray());
    }
    public PalmUserInfo ReadUserInfo() => ParseUserInfo(Main(Call(0x10), 30));
    public static PalmUserInfo ParseUserInfo(ReadOnlySpan<byte> b)
    {
        if (b.Length < 30 || b.Length < 30 + b[28] + b[29]) throw new ProtocolException("DLP user info length");
        return new(Text(b.Slice(30, b[28])), U32(b, 0), U32(b, 4), U32(b, 8), Date(b.Slice(12, 8)), Date(b.Slice(20, 8)));
    }
    public DateTime ReadSysDateTime() => Date(Main(Call(0x13), 8)) ?? throw new ProtocolException("DLP system date is empty");
    public void SetSysDateTime(DateTime time) => Call(0x14, new DlpArgument(0x20, EncodeDate(time)));
    public PalmAppPreference? ReadAppPreference(string creator, ushort id, bool saved = true, ushort maxBytes = 0xffff)
    {
        var request = new byte[10]; FourCC(creator).CopyTo(request, 0); Put16(request, 4, id); Put16(request, 6, maxBytes); request[8] = (byte)(saved ? 0x80 : 0);
        byte[] b;
        try { b = Main(Call(0x34, new DlpArgument(0x20, request)), 6); }
        catch (DlpException ex) when (ex.Code == 5) { return null; }
        int size = U16(b, 4);
        if (b.Length - 6 < size) throw new ProtocolException("DLP preference size");
        return new(U16(b, 0), U16(b, 2), b.AsSpan(6, size).ToArray());
    }
    public void WriteAppPreference(string creator, ushort id, ushort version, byte[] data, bool saved = true)
    {
        if (data.Length > 65523) throw new InvalidDataException("DLP preference exceeds 64K command limit");
        var b = new byte[12 + data.Length]; FourCC(creator).CopyTo(b, 0); Put16(b, 4, id); Put16(b, 6, version); Put16(b, 8, (ushort)data.Length); b[10] = (byte)(saved ? 0x80 : 0); data.CopyTo(b, 12);
        Call(0x35, new DlpArgument(0x20, b));
    }
    public void ResetSystem() => Call(0x29);
    public void OpenConduit() => Call(0x2e);
    public IReadOnlyList<PalmDatabase> ReadDBList() => ReadDBList(false);
    public IReadOnlyList<PalmDatabase> ReadDBList(bool includeRom)
    {
        var ram = ReadDBListCore(0x80);
        return includeRom ? ram.Concat(ReadDBListCore(0x40).Select(db => db with { IsRom = true })).ToArray() : ram;
    }
    public IReadOnlyList<PalmDatabase> ReadRomDBList() => ReadDBListCore(0x40).Select(db => db with { IsRom = true }).ToArray();
    private IReadOnlyList<PalmDatabase> ReadDBListCore(byte storage)
    {
        var all = new List<PalmDatabase>(); int start = 0;
        while (true)
        {
            byte[] b;
            try { b = Main(Call(0x16, new DlpArgument(0x20, [(byte)(storage | (multiple ? 0x20 : 0)), 0, (byte)(start >> 8), (byte)start])), 4); }
            catch (DlpException ex) when (multiple && ex.Code is 4 or 13) { multiple = false; continue; }
            catch (DlpException ex) when (ex.Code == 5) { return all; }
            var page = ParseDBList(b); all.AddRange(page);
            if ((b[2] & 0x80) == 0) return all;
            int next = U16(b, 0) + 1;
            if (page.Count == 0 || next <= start || next > 65535) throw new ProtocolException("DLP DB continuation made no progress"); start = next;
        }
    }
    public static IReadOnlyList<PalmDatabase> ParseDBList(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4) throw new ProtocolException("DLP DB list header");
        var list = new List<PalmDatabase>(); int offset = 4;
        for (int i = 0; i < b[3]; i++)
        {
            if (offset >= b.Length) throw new ProtocolException("DLP DB entry missing"); int size = b[offset];
            if (size < 45 || size > b.Length - offset) throw new ProtocolException("DLP DB entry size"); var entry = b.Slice(offset, size);
            list.Add(new(Text(entry[44..]), Encoding.ASCII.GetString(entry.Slice(4, 4)), Encoding.ASCII.GetString(entry.Slice(8, 4)), U16(entry, 2), U16(entry, 12), U32(entry, 14), Date(entry.Slice(26, 8)), U16(entry, 42), entry[1], Date(entry.Slice(18, 8)), Date(entry.Slice(34, 8))));
            offset += size;
        }
        if (offset != b.Length) throw new ProtocolException("DLP DB trailing bytes"); return list;
    }
    public void AddSyncLogEntry(string text) => Call(0x2a, new DlpArgument(0x20, PalmText.Encode(text + '\0')));
    public void EndOfSync(ushort code = 0) => Call(0x2f, new DlpArgument(0x20, [(byte)(code >> 8), (byte)code]));
    private static ushort U16(ReadOnlySpan<byte> b, int offset) => BinaryPrimitives.ReadUInt16BigEndian(b[offset..]);
    private static uint U32(ReadOnlySpan<byte> b, int offset) => BinaryPrimitives.ReadUInt32BigEndian(b[offset..]);
    private static string Text(ReadOnlySpan<byte> b) => PalmText.Decode(b);
    private static DateTime? Date(ReadOnlySpan<byte> b)
    {
        int year = U16(b, 0); if (year == 0) return null;
        try { return new DateTime(year, b[2], b[3], b[4], b[5], b[6], DateTimeKind.Unspecified); } catch (ArgumentOutOfRangeException) { throw new ProtocolException("DLP invalid date"); }
    }
}
