using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;

namespace PiloSync.Cli;

public static class TrayIcons
{
    public const int FrameCount = 8;
    private static readonly int[] IdleSizes = [16, 32, 48], PausedSizes = [16, 32, 48];
    public static Bitmap Render(int size, int frame, bool paused)
    {
        if (size < 16 || size > 256) throw new ArgumentOutOfRangeException(nameof(size));
        var sizes = paused ? PausedSizes : IdleSizes;
        int sourceSize = sizes.FirstOrDefault(s => s >= size, sizes[^1]);
        using var stream = typeof(TrayIcons).Assembly.GetManifestResourceStream($"PiloSync.Cli.Resources.{(paused ? "paused" : "idle")}-{sourceSize}.png")
            ?? throw new InvalidOperationException("Tray icon resource is missing.");
        using var source = new Bitmap(stream);
        frame = paused ? 0 : ((frame % FrameCount) + FrameCount) % FrameCount;
        if (sourceSize == size && frame == 0) return (Bitmap)source.Clone();
        using var scaled = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.DrawImage(source, new Rectangle(0, 0, size, size), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        }
        if (frame == 0) return (Bitmap)scaled.Clone();
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.TranslateTransform(size / 2f, size / 2f); g.RotateTransform(frame * 45); g.TranslateTransform(-size / 2f, -size / 2f);
            g.DrawImage(scaled, new Rectangle(0, 0, size, size), 0, 0, size, size, GraphicsUnit.Pixel, attributes);
        }
        return bitmap;
    }
    public static Icon Create(int size, int frame, bool paused)
    {
        using var bitmap = Render(size, frame, paused);
        using var stream = new MemoryStream();
        Write(stream, [(size, Png(bitmap))]);
        stream.Position = 0;
        using var icon = new Icon(stream, size, size);
        return (Icon)icon.Clone();
    }
    private static byte[] Png(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png); return stream.ToArray();
    }
    private static void Write(Stream output, (int Size, byte[] Data)[] images)
    {
        if (images.Length == 0 || images.Length > ushort.MaxValue) throw new ArgumentException("Invalid icon count.", nameof(images));
        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)images.Length);
        int offset = 6 + images.Length * 16;
        foreach (var (size, data) in images)
        {
            writer.Write((byte)(size == 256 ? 0 : size)); writer.Write((byte)(size == 256 ? 0 : size));
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(data.Length); writer.Write(offset); offset += data.Length;
        }
        foreach (var (_, data) in images) writer.Write(data);
    }
}

