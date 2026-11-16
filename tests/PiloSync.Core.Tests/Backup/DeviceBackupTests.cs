using PiloSync.Protocol;
using static PiloSync.Core.Tests.DlpFixtures;
using static PiloSync.Core.Tests.DbFixtures;
using Xunit;
namespace PiloSync.Core.Tests;

public class DeviceBackupTests
{
    private static Fake BackupDevice(ushort recordError = 0) => new(req => req[0] switch
    {
        0x17 => Reply(0x17,[1]), 0x2b => Reply(0x2b,[0,1]), 0x1b or 0x1d => Reply(req[0], error:5),
        0x20 => recordError != 0 ? Reply(0x20,error:recordError) : Reply(0x20,Convert.FromHexString("000000010000000200030405")), 0x19 => Reply(0x19), _ => throw new Exception("Unexpected request")
    });
    [Fact]
    public void BackupReportsProgressExcludingRomIncludingUnchangedAndCollisions()
    {
        using var folder=new Folder(); var db=Metadata(Sample());
        File.WriteAllBytes(System.IO.Path.Combine(folder.Path,db.Name+".pdb"),Bytes(Sample()));
        var fake=BackupDevice(); var progress=new List<SyncProgress>(); var logged=new List<string>();
        var result=DeviceBackup.Run(new(fake),[db with {IsRom=true},db,db with {Name="Other"},db with {Name="a/b"},db with {Name="a:b"}],folder.Path,line=>
        {
            Assert.NotEmpty(progress); Assert.Contains(progress[^1].Name,line); logged.Add(progress[^1].Name);
        },progress.Add);
        Assert.Equal(new[] { new SyncProgress(SyncPhase.Backup,1,4,db.Name), new(SyncPhase.Backup,2,4,"Other"), new(SyncPhase.Backup,3,4,"a/b"), new(SyncPhase.Backup,4,4,"a:b") },progress);
        Assert.Equal(new[]{db.Name,"Other","a/b","a:b"},logged); Assert.Single(result.Skipped); Assert.Single(result.Saved); Assert.Equal(2,result.FailedCount);
    }
    [Fact]
    public void BackupSavesThenSkipsWithoutDeviceReads()
    {
        using var folder = new Folder(); var db = Metadata(Sample()); var fake = BackupDevice(); var dlp = new DlpClient(fake);
        Assert.Equal(1,DeviceBackup.Run(dlp,[db],folder.Path,_=>{}).SavedCount); int calls = fake.Requests.Count;
        Assert.Equal(1,DeviceBackup.Run(dlp,[db],folder.Path,_=>{}).SkippedCount); Assert.Equal(calls,fake.Requests.Count);
        using var input = File.OpenRead(System.IO.Path.Combine(folder.Path,"たっち.pdb")); Assert.Equal(new byte[] {4,5},Assert.Single(PalmDbFile.Read(input).Records).Data);
    }
    [Fact]
    public void SavedLogShowsPreviousAndCurrentModificationNumberAndModified()
    {
        using var folder = new Folder(); var db = Metadata(Sample()) with { ModificationNumber = 8, Modified = When.AddHours(4) }; var lines = new List<string>();
        File.WriteAllBytes(System.IO.Path.Combine(folder.Path, db.Name + ".pdb"), Bytes(Sample()));
        DeviceBackup.Run(new(BackupDevice()), [db], folder.Path, lines.Add);
        Assert.Matches(@"^保存 たっち \(\d+ バイト, \d+\.\d 秒\) / 修正番号 7→8 / 変更日時 2026-10-25 12:34:56→2026-10-25 16:34:56$", Assert.Single(lines));
    }
    [Fact]
    public void SavedLogShowsNoPreviousOnFirstBackup()
    {
        using var folder = new Folder(); var lines = new List<string>();
        DeviceBackup.Run(new(BackupDevice()), [Metadata(Sample())], folder.Path, lines.Add);
        Assert.Matches(@"^保存 たっち \(\d+ バイト, \d+\.\d 秒\) / 修正番号 前回なし→7 / 変更日時 前回なし→2026-10-25 12:34:56$", Assert.Single(lines));
    }
    [Fact]
    public void BackupFailureKeepsPreviousFileAndClosesHandle()
    {
        using var folder = new Folder(); var old=Bytes(Sample()); string path=System.IO.Path.Combine(folder.Path,"たっち.pdb"); File.WriteAllBytes(path,old);
        var fake=BackupDevice(10); var db=Metadata(Sample()) with { ModificationNumber=8 };
        Assert.Equal(1,DeviceBackup.Run(new(fake),[db],folder.Path,_=>{}).FailedCount); Assert.Equal(old,File.ReadAllBytes(path)); Assert.Equal(0x19,fake.Requests[^1][0]); Assert.Single(Directory.GetFiles(folder.Path));
    }
    [Fact]
    public void BackupCommunicationFailureEscapesAfterClose()
    {
        using var folder=new Folder(); var fake=new Fake(req=>req[0] switch { 0x17=>Reply(0x17,[1]),0x19=>Reply(0x19),_=>throw new IOException("removed") });
        Assert.Throws<IOException>(()=>DeviceBackup.Run(new(fake),[Metadata(Sample())],folder.Path,_=>{})); Assert.Equal(0x19,fake.Requests[^1][0]);
    }
    [Fact]
    public void BackupRejectsCollidingNamesAndExcludesRom()
    {
        using var folder=new Folder(); var fake=new Fake(_=>throw new Exception("must not read")); var db=Metadata(Sample());
        var result=DeviceBackup.Run(new(fake),[db with {Name="a/b"},db with {Name="a:b"},db with {IsRom=true}],folder.Path,_=>{}); Assert.Equal(2,result.FailedCount); Assert.Empty(fake.Requests);
    }
    [Fact]
    public void FailedAtomicReplaceLeavesOldBackupAndNoTemporaryFile()
    {
        using var folder=new Folder(); string path=System.IO.Path.Combine(folder.Path,"\u305f\u3063\u3061.pdb"); byte[] old=Bytes(Sample()); File.WriteAllBytes(path,old);
        using (var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        { var result=DeviceBackup.Run(new(BackupDevice()),[Metadata(Sample()) with {ModificationNumber=8}],folder.Path,_=>{}); Assert.Single(result.Failed); }
        Assert.Equal(old,File.ReadAllBytes(path)); Assert.Single(Directory.GetFiles(folder.Path));
    }
    [Fact]
    public void ResourceBackupAndInstallFlow()
    {
        using var folder=new Folder(); var sample=Sample(true);
        var fake=new Fake(req=>req[0] switch {0x17=>Reply(0x17,[1]),0x2b=>Reply(0x2b,[0,1]),0x1b or 0x1d=>Reply(req[0],error:5),0x23=>Reply(0x23,Convert.FromHexString("636F64650001000000020405")),0x19=>Reply(0x19),_=>throw new Exception("Unexpected request")});
        Assert.Single(DeviceBackup.Run(new(fake),[Metadata(sample)],folder.Path,_=>{}).Saved);
        var install=new Fake(req=>req[0] switch {0x16=>Reply(0x16,error:5),0x15=>Reply(0x15,error:13),0x18=>Reply(0x18,[1]),0x24 or 0x19=>Reply(req[0]),_=>throw new Exception("Unexpected request")});
        Assert.Single(DeviceInstaller.Run(new(install),folder.Path,_=>{}).Succeeded); Assert.Contains(install.Requests,r=>r[0]==0x24); Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [Theory]
    [InlineData(1, "netl", 0, true)] [InlineData(7, "psys", 0, true)]
    [InlineData(10, "TEST", 0x8000, true)] [InlineData(12, "TEST", 0x8000, true)]
    [InlineData(3, "netl", 0x8000, false)] [InlineData(10, "TEST", 0, false)]
    public void BackupClassifiesOnlyInUseSystemErrorsAndKeepsExistingFiles(ushort error, string creator, ushort attributes, bool inUse)
    {
        using var folder = new Folder(); using var archive = new Folder();
        var old = new PalmDbFile { Name = "DevicePatchVFSCacheDB", Type = "DATA", Creator = creator };
        foreach (string extension in new[] { ".pdb", ".prc" }) File.WriteAllBytes(Path.Combine(folder.Path, old.Name + extension), Bytes(old));
        var db = Metadata(old) with { Attributes = attributes, ModificationNumber = 1 };
        var lines = new List<string>();
        var result = DeviceBackup.Run(new(new Fake(req => Reply(req[0], error: error))), [db], folder.Path, lines.Add);
        Assert.Equal(inUse ? 0 : 1, result.FailedCount); Assert.Equal(inUse ? 1 : 0, result.InUse.Count);
        Assert.Contains(lines, l => l.Contains(inUse ? "バックアップ使用中で読めず" : "バックアップ失敗"));
        Assert.Contains(lines, l => l.Contains($"属性 0x{attributes:X4}") && l.Contains($"作成者 {creator}"));
        Assert.Equal(0, BackupTidy.Run(folder.Path, archive.Path, _ => { }, confirmed: result.ConfirmedFiles, deviceNames: new HashSet<string> { db.Name }));
        Assert.Equal(2, Directory.GetFiles(folder.Path).Length);
    }
}
