using System.Text;

namespace PiloSync.Core;

public sealed class PalmAccount
{
    public uint Id { get; set; }
    public string Name { get; set; } = "";
    public string Folder { get; set; } = "";
    public ushort Flags { get; set; } = 1;
    public byte[] Password { get; set; } = [];
    public List<UserSyncPreference> Permanent { get; } = [];
    public List<UserSyncPreference> Temporary { get; } = [];
    internal ArchiveString? OriginalName, OriginalFolder;
    internal ClassTag? Tag;
}
public sealed class UserSyncPreference
{
    // The numeric value preserves all four creator bytes without text conversion.
    public uint Creator { get; set; }
    public uint Action { get; set; }
    internal ClassTag? Tag;
}
internal sealed record ClassTag(string Name, ushort Schema, bool Definition, object Identity);
internal sealed record ArchiveString(string Value, byte[] Raw);

public sealed class UsersDat
{
    private static readonly Encoding Cp932;
    static UsersDat()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp932 = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
    public List<PalmAccount> Users { get; } = [];
    public ushort UserSchema { get; set; } = 1;
    public ushort PreferenceSchema { get; set; } = 1;

    public static UsersDat Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        using var r = new BinaryReader(stream);
        var result = new UsersDat();
        var classes = new Dictionary<int, ClassTag>();
        int seq = 0;
        ClassTag Tag(string expected)
        {
            ushort value = r.ReadUInt16();
            ClassTag tag;
            if (value == 0xffff)
            {
                ushort schema = r.ReadUInt16();
                string name = Encoding.ASCII.GetString(Take(r, r.ReadUInt16()));
                if (name != expected) throw new InvalidDataException($"未知のクラス: {name}");
                tag = new(name, schema, true, new object());
                classes.Add(++seq, tag);
                ++seq;
            }
            else
            {
                if ((value & 0x8000) == 0 || !classes.TryGetValue(value & 0x7fff, out var reference) || reference.Name != expected)
                    throw new InvalidDataException($"未知のクラスタグ: {value:X4}");
                tag = reference with { Definition = false };
                ++seq;
            }
            return tag;
        }
        int count = r.ReadUInt16();
        for (int i = 0; i < count; i++)
        {
            var tag = Tag("CUser");
            result.UserSchema = tag.Schema;
            var user = new PalmAccount { Tag = tag, Id = r.ReadUInt32() };
            user.OriginalName = ReadString(r); user.Name = user.OriginalName.Value;
            user.OriginalFolder = ReadString(r); user.Folder = user.OriginalFolder.Value;
            user.Flags = r.ReadUInt16();
            user.Password = Take(r, checked((int)r.ReadUInt32()));
            foreach (var list in new[] { user.Permanent, user.Temporary })
            {
                int n = r.ReadUInt16();
                for (int j = 0; j < n; j++)
                {
                    var pt = Tag("CUserSyncPreference");
                    result.PreferenceSchema = pt.Schema;
                    list.Add(new() { Tag = pt, Creator = r.ReadUInt32(), Action = r.ReadUInt32() });
                }
            }
            result.Users.Add(user);
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("users.dat の末尾に未知のデータがあります。");
        return result;
    }
    private static byte[] Take(BinaryReader r, int n)
    {
        if (n < 0 || n > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("users.dat が途中で切れています。");
        return r.ReadBytes(n);
    }
    private static ArchiveString ReadString(BinaryReader r)
    {
        long start = r.BaseStream.Position;
        bool unicode = false;
        uint Length()
        {
            byte b = r.ReadByte();
            if (b < 255) return b;
            ushort w = r.ReadUInt16();
            if (w == 0xfffe)
            {
                if (unicode) throw new InvalidDataException("CString の Unicode マーカーが重複しています。");
                unicode = true;
                return Length();
            }
            return w == 0xffff ? r.ReadUInt32() : w;
        }
        uint length = Length();
        byte[] data = Take(r, checked((int)length * (unicode ? 2 : 1)));
        string value = (unicode ? Encoding.Unicode : Cp932).GetString(data);
        long end = r.BaseStream.Position;
        r.BaseStream.Position = start;
        byte[] raw = Take(r, checked((int)(end - start)));
        return new(value, raw);
    }
    private static void WriteString(BinaryWriter w, string value, ArchiveString? original)
    {
        if (original?.Value == value) { w.Write(original.Raw); return; }
        byte[] data = Cp932.GetBytes(value);
        if (data.Length < 255) w.Write((byte)data.Length);
        else if (data.Length < 0xfffe) { w.Write((byte)255); w.Write((ushort)data.Length); }
        else { w.Write((byte)255); w.Write((ushort)0xffff); w.Write((uint)data.Length); }
        w.Write(data);
    }
    public byte[] Write()
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        var classes = new Dictionary<(string, ushort), int>();
        var originalClasses = new Dictionary<object, int>();
        int seq = 0;
        void Tag(string name, ushort schema, ClassTag? original)
        {
            schema = original?.Schema ?? schema;
            var key = (name, schema);
            int id;
            bool found = original is null ? classes.TryGetValue(key, out id) : originalClasses.TryGetValue(original.Identity, out id);
            if (original?.Definition == true || !found)
            {
                w.Write((ushort)0xffff); w.Write(schema);
                byte[] text = Encoding.ASCII.GetBytes(name);
                w.Write((ushort)text.Length); w.Write(text);
                classes[key] = ++seq;
                if (original is not null) originalClasses[original.Identity] = seq;
                ++seq;
            }
            else
            {
                if (id >= 0x7fff) throw new InvalidDataException("クラス参照が多すぎます。");
                w.Write((ushort)(0x8000 | id)); ++seq;
            }
        }
        w.Write(checked((ushort)Users.Count));
        foreach (var u in Users)
        {
            Tag("CUser", UserSchema, u.Tag);
            w.Write(u.Id); WriteString(w, u.Name, u.OriginalName); WriteString(w, u.Folder, u.OriginalFolder);
            w.Write(u.Flags); w.Write((uint)u.Password.Length); w.Write(u.Password);
            foreach (var list in new[] { u.Permanent, u.Temporary })
            {
                w.Write(checked((ushort)list.Count));
                foreach (var p in list) { Tag("CUserSyncPreference", PreferenceSchema, p.Tag); w.Write(p.Creator); w.Write(p.Action); }
            }
        }
        return stream.ToArray();
    }
}
