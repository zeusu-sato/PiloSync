using System.Text;
namespace PiloSync.Protocol;

public static class PalmText
{
    static PalmText() { System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); Encoding = System.Text.Encoding.GetEncoding(932); }
    public static Encoding Encoding { get; }
    public static string Decode(ReadOnlySpan<byte> bytes) { int end = bytes.IndexOf((byte)0); return Encoding.GetString(end < 0 ? bytes : bytes[..end]); }
    public static byte[] Encode(string text) => Encoding.GetBytes(text);
}
