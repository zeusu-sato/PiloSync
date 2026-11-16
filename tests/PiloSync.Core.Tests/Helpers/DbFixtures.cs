using PiloSync.Protocol;
namespace PiloSync.Core.Tests;

internal static class DbFixtures
{
    internal static readonly DateTime When = new(2026, 10, 25, 12, 34, 56);
    internal static PalmDbFile Sample(bool resource = false)
    {
        var db = new PalmDbFile { Name = "たっち", Attributes = (ushort)(resource ? 9 : 8), Version = 2, Created = When.AddDays(-1), Modified = When, BackupDate = When, ModificationNumber = 7, Type = resource ? "appl" : "DATA", Creator = "TEST", UniqueIdSeed = 0x123457, AppInfo = [1, 2], SortInfo = [3] };
        if (resource) { db.Resources.Add(new("code", 1, [4, 5])); db.Resources.Add(new("tSTR", 2, [])); }
        else { db.Records.Add(new(0x123456, 0x50, 3, [4, 5])); db.Records.Add(new(8, 0, 0, [])); }
        return db;
    }
    internal static byte[] Bytes(PalmDbFile db) { using var s = new MemoryStream(); db.Write(s); return s.ToArray(); }
    internal static PalmDatabase Metadata(PalmDbFile file) => new(file.Name,file.Type,file.Creator,file.Attributes,file.Version,file.ModificationNumber,file.Modified,0,0x40,file.Created,file.BackupDate);
    internal static byte[] DatabasePage(string name)
    {
        byte[] text=PalmText.Encode(name+"\0"); var b=new byte[48+text.Length]; b[3]=1; b[4]=(byte)(44+text.Length); "DATATEST"u8.CopyTo(b.AsSpan(8)); text.CopyTo(b,48); return b;
    }
}
