using System.Drawing;
using PiloSync.Cli;
using Xunit;

namespace PiloSync.Core.Tests;

public sealed class TrayIconTests
{
    private static int[] Pixels(Bitmap bitmap) => Enumerable.Range(0, bitmap.Height).SelectMany(y => Enumerable.Range(0, bitmap.Width).Select(x => bitmap.GetPixel(x, y).ToArgb())).ToArray();
    private static string Name(string state, int size) => $"PiloSync.Cli.Resources.{state}-{size}.png";
    private static byte[] Resource(string name)
    {
        using var stream = typeof(TrayIcons).Assembly.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray();
    }
    private static int[] Sizes(string state) => typeof(TrayIcons).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith($"PiloSync.Cli.Resources.{state}-") && n.EndsWith(".png"))
        .Select(n => int.Parse(n.Split('-')[^1][..^4])).Order().ToArray();
    [Fact]
    public void EmbeddedImagesHaveMatchingSizes()
    {
        Assert.Equal(new[] { 16, 32, 48 }, Sizes("idle"));
        Assert.Equal(Sizes("idle"), Sizes("paused"));
        foreach (string state in new[] { "idle", "paused" })
        foreach (int size in Sizes(state))
        {
            using var stream = new MemoryStream(Resource(Name(state, size)));
            using var bitmap = new Bitmap(stream);
            Assert.Equal(new Size(size, size), bitmap.Size);
        }
    }
    [Theory]
    [InlineData(16)] [InlineData(20)] [InlineData(24)] [InlineData(32)] [InlineData(40)] [InlineData(48)] [InlineData(64)]
    public void RenderAndCreateSupportAllFrames(int size)
    {
        foreach (bool paused in new[] { false, true })
        for (int frame = 0; frame < TrayIcons.FrameCount; frame++)
        {
            using var bitmap = TrayIcons.Render(size, frame, paused);
            using var icon = TrayIcons.Create(size, frame, paused);
            Assert.Equal(new Size(size, size), bitmap.Size); Assert.Equal(bitmap.Size, icon.Size);
            if (paused) { using var idle = TrayIcons.Render(size, 0, true); Assert.Equal(Pixels(idle), Pixels(bitmap)); }
        }
    }
    [Theory]
    [InlineData(16)] [InlineData(32)] [InlineData(48)]
    public void FrameZeroMatchesResource(int size)
    {
        foreach (bool paused in new[] { false, true })
        {
            using var stream = new MemoryStream(Resource(Name(paused ? "paused" : "idle", size)));
            using var source = new Bitmap(stream);
            using var actual = TrayIcons.Render(size, 0, paused);
            Assert.Equal(Pixels(source), Pixels(actual));
        }
    }
    [Theory]
    [InlineData(16)] [InlineData(20)] [InlineData(24)] [InlineData(32)] [InlineData(40)] [InlineData(48)] [InlineData(64)]
    public void FramesRotateAndWrap(int size)
    {
        Assert.Equal(8, TrayIcons.FrameCount);
        using var idle = TrayIcons.Render(size, 0, false);
        using var wrapped = TrayIcons.Render(size, TrayIcons.FrameCount, false);
        using var negative = TrayIcons.Render(size, -TrayIcons.FrameCount, false);
        Assert.Equal(Pixels(idle), Pixels(wrapped)); Assert.Equal(Pixels(idle), Pixels(negative));
        for (int frame = 0; frame < TrayIcons.FrameCount; frame++)
        {
            using var a = TrayIcons.Render(size, frame, false);
            using var b = TrayIcons.Render(size, frame + 1, false);
            Assert.False(Pixels(a).SequenceEqual(Pixels(b)));
        }
    }
    [Theory]
    [InlineData(16)] [InlineData(32)] [InlineData(48)]
    public void PausedIsGrayAndIdleHasGreenAndOrange(int size)
    {
        using var idle = TrayIcons.Render(size, 0, false);
        using var paused = TrayIcons.Render(size, 0, true);
        var colors = Pixels(paused).Select(Color.FromArgb).Where(c => c.A > 0).ToArray();
        Assert.NotEmpty(colors);
        Assert.All(colors, c => { Assert.InRange(Math.Abs(c.R - c.G), 0, 1); Assert.InRange(Math.Abs(c.G - c.B), 0, 1); });
        Assert.Contains(Pixels(idle).Select(Color.FromArgb), c => c.A == 255 && c.G > c.R + 20 && c.G > c.B + 20);
        Assert.Contains(Pixels(idle).Select(Color.FromArgb), c => c.A == 255 && c.R > c.G + 20 && c.G > c.B + 20);
    }
    [Theory]
    [InlineData(15)] [InlineData(257)]
    public void InvalidSizesAreRejected(int size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TrayIcons.Render(size, 0, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrayIcons.Create(size, 0, false));
    }
    [Fact]
    public void CommittedIconContainsEmbeddedIdlePngs()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PiloSync.ico"));
        using var reader = new BinaryReader(stream);
        Assert.Equal(0, reader.ReadUInt16()); Assert.Equal(1, reader.ReadUInt16());
        int count = reader.ReadUInt16(); var sizes = new List<int>();
        for (int i = 0; i < count; i++)
        {
            stream.Position = 6 + i * 16;
            int size = reader.ReadByte(); if (size == 0) size = 256;
            int height = reader.ReadByte(); if (height == 0) height = 256;
            Assert.Equal(size, height); sizes.Add(size);
            stream.Position += 6;
            int length = reader.ReadInt32(), offset = reader.ReadInt32();
            stream.Position = offset;
            Assert.Equal(Resource(Name("idle", size)), reader.ReadBytes(length));
        }
        Assert.Equal(Sizes("idle"), sizes.Order().ToArray());
    }
}
