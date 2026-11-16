using PiloSync.Protocol;
using static PiloSync.Core.Tests.DlpFixtures;
using static PiloSync.Core.Tests.DbFixtures;
using Xunit;
namespace PiloSync.Core.Tests;

public class DeviceInstallerTests
{
    [Fact]
    public void InsufficientMemoryRetainsFileAndSkipsCreate()
    {
        using var folder = new Folder();
        string path = System.IO.Path.Combine(folder.Path, "a.pdb");
        File.WriteAllBytes(path, Bytes(Sample()));
        var log = new List<string>();
        var memory = new byte[30]; memory[27] = 1;
        var fake = new Fake(req => req[0] switch { 0x16 => Reply(0x16, error: 5), 0x15 => Reply(0x15, memory), 0x2a => Reply(0x2a), _ => throw new Exception("Unexpected request") });
        var result = DeviceInstaller.Run(new(fake), folder.Path, log.Add);
        Assert.Single(result.Skipped!); Assert.Empty(result.Failed); Assert.True(File.Exists(path));
        Assert.DoesNotContain(fake.Requests, r => r[0] is 0x18 or 0x1a);
        Assert.Contains(log, line => line.Contains("インストール見送り a.pdb"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UnknownMemoryDoesNotSkipInstall(bool overwrite)
    {
        using var folder = new Folder(); var sample = Sample(); string path = System.IO.Path.Combine(folder.Path, "a.pdb"); File.WriteAllBytes(path, Bytes(sample));
        var memory = new byte[30]; if (overwrite) memory[27] = 1;
        var fake = new Fake(req => req[0] switch { 0x16 => overwrite && (req[4] & 0x80) != 0 ? Reply(0x16, DatabasePage(sample.Name)) : Reply(0x16, error: 5), 0x15 => Reply(0x15, memory), 0x18 => Reply(0x18, [1]), 0x1a or 0x19 or 0x1c or 0x1e => Reply(req[0]), 0x21 => Reply(0x21, [0, 0, 0, 1]), _ => throw new Exception("Unexpected request") });
        var result = DeviceInstaller.Run(new(fake), folder.Path, _ => { });
        Assert.Single(result.Succeeded); Assert.Empty(result.Skipped!); Assert.False(File.Exists(path));
        if (overwrite) { Assert.Contains(fake.Requests, r => r[0] == 0x1a); Assert.DoesNotContain(fake.Requests, r => r[0] == 0x39); }
    }
    private static Fake InstallDevice(bool memoryError=false) => new(req=>req[0] switch
    {
        0x16=>Reply(0x16,error:5), 0x15=>Reply(0x15,error:13), 0x18=>Reply(0x18,[1]), 0x1c or 0x1e or 0x19 or 0x1a=>Reply(req[0]),
        0x21=>memoryError ? Reply(0x21,error:3) : Reply(0x21,[0,0,0,1]), _=>throw new Exception("Unexpected install request")
    });
    [Fact]
    public void InstallReportsProgressIncludingInvalidFileBeforeLogging()
    {
        using var folder=new Folder();
        File.WriteAllBytes(System.IO.Path.Combine(folder.Path,"a.pdb"),Bytes(Sample()));
        File.WriteAllBytes(System.IO.Path.Combine(folder.Path,"bad.pdb"),[1,2]);
        File.WriteAllBytes(System.IO.Path.Combine(folder.Path,"c.pqa"),Bytes(Sample()));
        var progress=new List<SyncProgress>(); var logged=new List<string>();
        var result=DeviceInstaller.Run(new(InstallDevice()),folder.Path,line=>
        {
            Assert.NotEmpty(progress); Assert.Contains(progress[^1].Name,line); logged.Add(progress[^1].Name);
        },p=>progress.Add(p));
        Assert.Equal(new[] { new SyncProgress(SyncPhase.Install,1,3,"a.pdb"), new(SyncPhase.Install,2,3,"bad.pdb"), new(SyncPhase.Install,3,3,"c.pqa") },progress);
        Assert.Equal(new[]{"a.pdb","bad.pdb","c.pqa"},logged); Assert.Equal(new[]{"bad.pdb"},result.Failed);
    }
    [Fact]
    public void InstallDeletesOnlySuccessfulFileAndIgnoresSubfolders()
    {
        using var folder=new Folder(); File.WriteAllBytes(System.IO.Path.Combine(folder.Path,"a.pdb"),Bytes(Sample())); File.WriteAllBytes(System.IO.Path.Combine(folder.Path,"bad.pdb"),[1,2]);
        string card=System.IO.Path.Combine(folder.Path,"card"); Directory.CreateDirectory(card); File.WriteAllBytes(System.IO.Path.Combine(card,"a.pdb"),Bytes(Sample()));
        var log=new List<string>(); var result=DeviceInstaller.Run(new(InstallDevice()),folder.Path,log.Add);
        Assert.Equal(new[]{"a.pdb"},result.Succeeded); Assert.Equal(new[]{"bad.pdb"},result.Failed); Assert.Equal(1,result.RemainingCount); Assert.True(File.Exists(System.IO.Path.Combine(card,"a.pdb"))); Assert.Contains(log,s=>s.Contains("未対応"));
    }
    [Fact]
    public void InstallMemoryErrorKeepsFileAndDeletesPartialDb()
    {
        using var folder=new Folder(); File.WriteAllBytes(System.IO.Path.Combine(folder.Path,"a.pdb"),Bytes(Sample())); var fake=InstallDevice(true);
        var result=DeviceInstaller.Run(new(fake),folder.Path,_=>{}); Assert.Empty(result.Succeeded); Assert.Single(result.Failed); Assert.Equal(1,result.RemainingCount); Assert.Equal(new byte[]{0x19,0x1a},fake.Requests.TakeLast(2).Select(r=>r[0]));
    }
    [Fact]
    public void InstallLockedSourceIsRetained()
    {
        using var folder=new Folder(); string path=System.IO.Path.Combine(folder.Path,"a.pdb"); File.WriteAllBytes(path,Bytes(Sample()));
        using var locked=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None); var fake=InstallDevice(); var result=DeviceInstaller.Run(new(fake),folder.Path,_=>{}); Assert.Single(result.Failed); Assert.Empty(fake.Requests);
    }
    [Theory]
    [InlineData(3)] [InlineData(16)]
    public void CreateFailureCleansUpAndContinues(ushort error)
    {
        using var folder=new Folder(); foreach (var name in new[]{"a.pdb","b.pdb"}) File.WriteAllBytes(System.IO.Path.Combine(folder.Path,name),Bytes(Sample()));
        int creates=0; var normal=InstallDevice();
        var fake=new Fake(req=> { if(req[0]==0x18 && ++creates==1) return Reply(0x18,error:error); normal.Send(req); return normal.Receive(); });
        var result=DeviceInstaller.Run(new(fake),folder.Path,_=>{}); Assert.Single(result.Succeeded); Assert.Single(result.Failed); Assert.Equal(1,result.RemainingCount); Assert.Contains(fake.Requests,r=>r[0]==0x1a);
    }
    [Fact]
    public void RomDatabaseIsNotDeletedOrInstalled()
    {
        using var folder=new Folder(); File.WriteAllBytes(System.IO.Path.Combine(folder.Path,"a.pdb"),Bytes(Sample()));
        var fake=new Fake(req=>req[0]==0x16 ? ((req[4]&0x80)!=0 ? Reply(0x16,error:5) : Reply(0x16,DatabasePage("\u305f\u3063\u3061"))) : throw new Exception("ROM must not be changed"));
        var result=DeviceInstaller.Run(new(fake),folder.Path,_=>{}); Assert.Single(result.Failed); Assert.Equal(1,result.RemainingCount); Assert.All(fake.Requests,r=>Assert.Equal(0x16,r[0]));
    }
    [Fact]
    public void InstallResourceTimeoutSkipsCloseDbAndPreservesExceptionAndStage()
    {
        using var folder=new Folder(); string path=System.IO.Path.Combine(folder.Path,"a.prc"); var sample=Sample(true);
        sample.Resources.Add(new("data",3,[6])); File.WriteAllBytes(path,Bytes(sample));
        int writes=0; var timeout=new TimeoutException("resource timeout"); var log=new List<string>();
        var fake=new Fake(req=>req[0] switch {0x16=>Reply(0x16,error:5),0x15=>Reply(0x15,error:13),0x18=>Reply(0x18,[1]),0x1c or 0x1e=>Reply(req[0]),0x24=>++writes==2 ? throw timeout : Reply(0x24),_=>throw new Exception("Unexpected request")});
        var actual=Assert.Throws<TimeoutException>(()=>DeviceInstaller.Run(new(fake),folder.Path,log.Add));
        Assert.Same(timeout,actual); Assert.True(File.Exists(path)); Assert.DoesNotContain(fake.Requests,r=>r[0]==0x19);
        Assert.Contains(log,s=>s.Contains("インストール中断 a.prc: Resource 2/3: resource timeout"));
    }
    [Fact]
    public void InstallerCachesDbListAndDeletesEarlierInstallWithSameName()
    {
        using var folder = new Folder(); var sample = Sample(); sample.Attributes |= 0x20;
        foreach (string name in new[] { "a.pdb", "b.pdb" }) File.WriteAllBytes(System.IO.Path.Combine(folder.Path, name), Bytes(sample));
        var fake = InstallDevice(); var result = DeviceInstaller.Run(new(fake), folder.Path, _ => { });
        Assert.Equal(2, result.Succeeded.Count); Assert.True(result.ResetRequired);
        Assert.Equal(2, fake.Requests.Count(r => r[0] == 0x16));
        Assert.Single(fake.Requests, r => r[0] == 0x1a);
        new DlpClient(fake = new Fake(_ => Reply(0x29))).ResetSystem();
        Assert.Equal(new byte[] { 0x29, 0 }, Assert.Single(fake.Requests));
    }

}
