using PiloSync.Core;
using Xunit;

namespace PiloSync.Core.Tests;

public sealed class UsersDatTests
{
    [Fact]
    public void StockSampleRoundTripsExactly()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "users.dat"));
        var data = UsersDat.Read(bytes);
        Assert.Equal(6, data.Users.Count);
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal((uint)(1001 + i), data.Users[i].Id);
            Assert.Equal($"Device{i + 1}", data.Users[i].Name);
            Assert.Equal($"Device{i + 1}", data.Users[i].Folder);
            Assert.Empty(data.Users[i].Password);
        }
        Assert.Equal(bytes, data.Write());
        data.Users.Add(new() { Id = 42, Name = "日本語", Folder = "追加", Flags = 0x8342, Password = [0, 255, 13] });
        data.Users[^1].Permanent.Add(new() { Creator = 0xDEADBEEF, Action = 0x12345678 });
        byte[] modified = data.Write();
        Assert.Equal(modified, UsersDat.Read(modified).Write());
        Assert.Equal(bytes[2..9], modified[2..9]);
    }
    [Theory]
    [InlineData(0)] [InlineData(254)] [InlineData(255)] [InlineData(65533)] [InlineData(65534)] [InlineData(65535)] [InlineData(65536)]
    public void CStringLengthBoundaries(int length)
    {
        var data = new UsersDat();
        data.Users.Add(new() { Id = 1, Name = new string('a', length), Folder = "日本語" });
        byte[] bytes = data.Write();
        Assert.Equal(data.Users[0].Name, UsersDat.Read(bytes).Users[0].Name);
        Assert.Equal(bytes, UsersDat.Read(bytes).Write());
        int nameStart = 2 + 2 + 2 + 2 + 5 + 4;
        Assert.Equal(length < 255 ? (byte)length : (byte)255, bytes[nameStart]);
        if (length >= 255) Assert.Equal(length < 65534 ? (ushort)length : (ushort)65535, BitConverter.ToUInt16(bytes, nameStart + 1));
    }
    [Fact]
    public void UnicodeAndNoncanonicalLengthsArePreserved()
    {
        // One user, Unicode CString "あ", then empty ANSI folder, unknown flags/password.
        byte[] bytes = Convert.FromHexString("0100FFFF34120500435573657201000000FFFEFF01" + "4230" + "FF0000" + "CDAB0300000000FF8000000000");
        var data = UsersDat.Read(bytes);
        Assert.Equal("あ", data.Users[0].Name);
        Assert.Equal((ushort)0x1234, data.UserSchema);
        Assert.Equal((ushort)0xabcd, data.Users[0].Flags);
        Assert.Equal(bytes, data.Write());
    }
    [Fact]
    public void UnknownTagsAndTruncationAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => UsersDat.Read([1, 0, 1, 128]));
        Assert.Throws<EndOfStreamException>(() => UsersDat.Read([1, 0]));
    }
    [Fact]
    public void ClassReferenceNumbersFollowObjectSequence()
    {
        var data = new UsersDat();
        data.Users.Add(new() { Id = 1, Name = "a", Folder = "a" });
        data.Users.Add(new() { Id = 2, Name = "b", Folder = "b" });
        data.Users[1].Temporary.Add(new() { Creator = 123, Action = 42 });
        data.Users[1].Temporary.Add(new() { Creator = 321, Action = 99 });
        var bytes = data.Write();
        // Preference class is #4 because the second user object consumes #3.
        Assert.Equal(new byte[] { 4, 128 }, bytes[^10..^8]);
        Assert.Equal(bytes, UsersDat.Read(bytes).Write());
    }
}
