using Microsoft.Win32;
using PiloSync.Cli;
using Xunit;

namespace PiloSync.Core.Tests;

public class PalmFileRegistrationTests
{
    [Fact]
    public void RegisterTwiceAndRemovePreservesOtherValues()
    {
        string path = @"Software\PiloSyncTest\" + Guid.NewGuid();
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(path);
            foreach (string ext in new[] { ".prc", ".pdb", ".pqa" })
            {
                using var key = root.CreateSubKey(ext);
                key.SetValue("", "InstallTool");
                using var open = key.CreateSubKey("OpenWithProgids");
                open.SetValue("Other.App", "other");
            }
            var registration = new PalmFileRegistration(root);
            const string exe = @"C:\Example Sync\PiloSync.exe";
            Assert.False(registration.IsRegistered());
            registration.Register(exe);
            registration.Register(exe);
            Assert.True(registration.IsRegistered());
            using (var key = root.OpenSubKey("PiloSync.PalmFile")) Assert.Equal("Palm のファイル", key!.GetValue("FriendlyTypeName"));
            using (var key = root.OpenSubKey(@"PiloSync.PalmFile\DefaultIcon")) Assert.Equal(exe + ",0", key!.GetValue(""));
            foreach (string keyPath in new[] { @"PiloSync.PalmFile\shell\open\command", @"Applications\PiloSync.exe\shell\open\command" })
                using (var key = root.OpenSubKey(keyPath)) Assert.Equal("\"" + exe + "\" \"%1\"", key!.GetValue(""));
            using (var key = root.OpenSubKey(@"Applications\PiloSync.exe")) Assert.Equal("PiloSync", key!.GetValue("FriendlyAppName"));
            foreach (string ext in new[] { ".prc", ".pdb", ".pqa" })
            {
                using var types = root.OpenSubKey(@"Applications\PiloSync.exe\SupportedTypes");
                Assert.Equal("", types!.GetValue(ext));
                using var open = root.OpenSubKey(ext + @"\OpenWithProgids");
                Assert.Equal("", open!.GetValue("PiloSync.PalmFile"));
            }
            registration.Unregister();
            registration.Unregister();
            Assert.False(registration.IsRegistered());
            Assert.Null(root.OpenSubKey(@"Applications\PiloSync.exe"));
            foreach (string ext in new[] { ".prc", ".pdb", ".pqa" })
            {
                using var key = root.OpenSubKey(ext);
                Assert.Equal("InstallTool", key!.GetValue(""));
                using var open = key.OpenSubKey("OpenWithProgids");
                Assert.Equal("other", open!.GetValue("Other.App"));
                Assert.Null(open.GetValue("PiloSync.PalmFile"));
            }
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, false); }
    }
}
