using System.Buffers.Binary;
using System.Text;
namespace PiloSync.Protocol;

public sealed class PalmDbFile
{
    private const int MaxFileSize = 64 * 1024 * 1024;
    private static readonly DateTime Epoch = new(1904, 1, 1);
    public string Name { get; set; } = "";
    public ushort Attributes { get; set; }
    public ushort Version { get; set; }
    public DateTime? Created { get; set; }
    public DateTime? Modified { get; set; }
    public DateTime? BackupDate { get; set; }
    public uint ModificationNumber { get; set; }
    public string Type { get; set; } = "DATA";
    public string Creator { get; set; } = "    ";
    public uint UniqueIdSeed { get; set; }
    public byte[] AppInfo { get; set; } = [];
    public byte[] SortInfo { get; set; } = [];
    public List<PalmRecord> Records { get; } = [];
    public List<PalmResource> Resources { get; } = [];
    public bool IsResource => (Attributes & 1) != 0;
    private static ushort U16(byte[] b, int p) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p));
    private static uint U32(byte[] b, int p) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p));
    private static void W16(byte[] b, int p, ushort n) => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(p), n);
    private static void W32(byte[] b, int p, uint n) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p), n);
    private static DateTime? FromSeconds(uint n) => n == 0 ? null : Epoch.AddSeconds(n);
    private static uint Seconds(DateTime? d)
    {
        if (d == null) return 0;
        double n = (DateTime.SpecifyKind(d.Value, DateTimeKind.Unspecified) - Epoch).TotalSeconds;
        if (n < 0 || n > uint.MaxValue) throw new InvalidDataException("Palm date outside 1904 epoch range");
        return (uint)n;
    }
    public static PalmDbFile Read(Stream stream)
    {
        using var copy = new MemoryStream(); var buffer = new byte[8192]; int n;
        while ((n = stream.Read(buffer)) != 0)
        { if (copy.Length + n > MaxFileSize) throw new InvalidDataException("Palm DB exceeds 64 MiB safety limit"); copy.Write(buffer, 0, n); }
        var b = copy.ToArray();
        if (b.Length < 78) throw new InvalidDataException("Short Palm DB header");
        if (b.AsSpan(0, 32).IndexOf((byte)0) < 0) throw new InvalidDataException("Unterminated Palm DB name");
        if (U32(b, 72) != 0) throw new InvalidDataException("Chained record lists are unsupported");
        var db = new PalmDbFile { Name = PalmText.Decode(b.AsSpan(0, 32)), Attributes = U16(b, 32), Version = U16(b, 34), Created = FromSeconds(U32(b, 36)), Modified = FromSeconds(U32(b, 40)), BackupDate = FromSeconds(U32(b, 44)), ModificationNumber = U32(b, 48), Type = Encoding.Latin1.GetString(b, 60, 4), Creator = Encoding.Latin1.GetString(b, 64, 4), UniqueIdSeed = U32(b, 68) };
        int count = U16(b, 76), entrySize = db.IsResource ? 10 : 8, dataStart = 78 + count * entrySize;
        if (dataStart > b.Length) throw new InvalidDataException("Short Palm DB entry table");
        var offsets = new List<int>();
        var labels = new List<string>();
        void Offset(uint value, string label)
        {
            if (value < dataStart || value > b.Length)
                throw new InvalidDataException($"Palm DB offset outside data: {label} offset 0x{value:X8} (valid 0x{dataStart:X8}..0x{b.Length:X8})");
            if (offsets.Count > 0 && value < offsets[^1])
                throw new InvalidDataException($"Unordered Palm DB offsets: {labels[^1]} offset 0x{offsets[^1]:X8} > {label} offset 0x{value:X8}");
            offsets.Add((int)value); labels.Add(label);
        }
        uint app = U32(b, 52), sort = U32(b, 56);
        if (app != 0) Offset(app, "AppInfo"); if (sort != 0) Offset(sort, "SortInfo");
        for (int i = 0; i < count; i++) Offset(U32(b, 78 + i * entrySize + (db.IsResource ? 6 : 0)), $"{(db.IsResource ? "resource" : "record")} {i}");
        offsets.Add(b.Length); int part = 0;
        byte[] Next() { int begin = offsets[part++]; return b[begin..offsets[part]]; }
        if (app != 0) db.AppInfo = Next(); if (sort != 0) db.SortInfo = Next();
        for (int i = 0; i < count; i++)
        {
            int p = 78 + i * entrySize;
            if (db.IsResource) db.Resources.Add(new(Encoding.Latin1.GetString(b, p, 4), U16(b, p + 4), Next()));
            else db.Records.Add(new((uint)(b[p + 5] << 16 | b[p + 6] << 8 | b[p + 7]), (byte)(b[p + 4] & 0xf0), (byte)(b[p + 4] & 0x0f), Next()));
        }
        return db;
    }
    public void Write(Stream stream)
    {
        int count = IsResource ? Resources.Count : Records.Count, size = IsResource ? 10 : 8;
        if (count > ushort.MaxValue || (IsResource ? Records.Count : Resources.Count) != 0) throw new InvalidDataException("Invalid Palm DB record list");
        var name = PalmText.Encode(Name); if (name.Length is 0 or > 31 || Name.Contains('\0')) throw new InvalidDataException("Invalid Palm DB name");
        long total = 80L + count * size + AppInfo.Length + SortInfo.Length + (IsResource ? Resources.Sum(r => (long)r.Data.Length) : Records.Sum(r => (long)r.Data.Length));
        if (total > MaxFileSize) throw new InvalidDataException("Palm DB exceeds 64 MiB safety limit");
        var header = new byte[80 + count * size]; name.CopyTo(header, 0); W16(header, 32, Attributes); W16(header, 34, Version);
        W32(header, 36, Seconds(Created)); W32(header, 40, Seconds(Modified)); W32(header, 44, Seconds(BackupDate)); W32(header, 48, ModificationNumber);
        uint offset = (uint)header.Length;
        if (AppInfo.Length != 0) { W32(header, 52, offset); offset += (uint)AppInfo.Length; }
        if (SortInfo.Length != 0) { W32(header, 56, offset); offset += (uint)SortInfo.Length; }
        DlpClient.FourCC(Type).CopyTo(header, 60); DlpClient.FourCC(Creator).CopyTo(header, 64); W32(header, 68, UniqueIdSeed); W16(header, 76, (ushort)count);
        for (int i = 0; i < count; i++)
        {
            int p = 78 + i * size;
            if (IsResource) { var r = Resources[i]; DlpClient.FourCC(r.Type).CopyTo(header, p); W16(header, p + 4, r.Id); W32(header, p + 6, offset); offset += (uint)r.Data.Length; }
            else { var r = Records[i]; if (r.UniqueId > 0xffffff || r.Category > 15) throw new InvalidDataException("Invalid Palm record ID/category"); W32(header, p, offset); header[p + 4] = (byte)((r.Attributes & 0xf0) | r.Category); header[p + 5] = (byte)(r.UniqueId >> 16); header[p + 6] = (byte)(r.UniqueId >> 8); header[p + 7] = (byte)r.UniqueId; offset += (uint)r.Data.Length; }
        }
        stream.Write(header); stream.Write(AppInfo); stream.Write(SortInfo);
        if (IsResource) foreach (var r in Resources) stream.Write(r.Data); else foreach (var r in Records) stream.Write(r.Data);
    }
}
