using System.Buffers.Binary;
using System.Text;

namespace PiloSync.Usb;

public sealed record UsbPortInfo(string FunctionId, int Port, int InEndpoint, int OutEndpoint, bool IsHotSync);

public sealed class UsbConnectionInfo
{
    public uint Ioctl { get; }
    public byte[] Raw { get; }
    public bool EndpointsGiven { get; }
    public IReadOnlyList<UsbPortInfo> Ports { get; }

    // Static evidence: Aceeca .sys 1467c/14885 and usbport.dll 66561b10.
    // Extended: byte count, byte endpointsGiven, ushort reserved; entries:
    // uint creator, byte port, byte packed endpoints (IN high nibble), ushort reserved.
    // Legacy: ushort count; entries byte function (2 = HotSync), byte port.
    public UsbConnectionInfo(uint ioctl, byte[] raw)
    {
        Ioctl = ioctl; Raw = (byte[])raw.Clone();
        bool legacy = ioctl == 0x222424;
        int header = legacy ? 2 : 4, stride = legacy ? 2 : 8;
        if (raw.Length < header) throw new IOException("Connection info: truncated header");
        int count = legacy ? BinaryPrimitives.ReadUInt16LittleEndian(raw) : raw[0];
        if (count == 0 || legacy && (count > 8 || (raw.Length - header) % stride != 0) || count > (raw.Length - header) / stride)
            throw new IOException($"Connection info: invalid/truncated port count {count}, bytes={raw.Length}");
        EndpointsGiven = !legacy && raw[1] == 1;
        var ports = new List<UsbPortInfo>();
        for (int i = 0; i < count; i++)
        {
            int offset = header + i * stride;
            string function = legacy ? raw[offset] switch { 0 => "generic", 1 => "debugger", 2 => "hotsync", 3 => "console", 4 => "remote file system", _ => $"function:{raw[offset]}" } : Encoding.ASCII.GetString(raw, offset, 4);
            uint creator = legacy ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset, 4));
            // USBTransport requests numeric creator 0x73796e63 ('sync' as a C multichar literal;
            // its little-endian buffer spelling is 'cnys'). Accept both spellings for diagnostics.
            bool sync = legacy ? raw[offset] == 2 : creator is 0x73796E63 or 0x636E7973;
            int port = raw[offset + (legacy ? 1 : 4)];
            int packed = legacy ? 0 : raw[offset + 5];
            ports.Add(new(function, port, EndpointsGiven ? packed >> 4 : port, EndpointsGiven ? packed & 15 : port, sync));
        }
        Ports = ports;
    }

    internal UsbPortInfo HotSyncPort()
    {
        var port = Ports.FirstOrDefault(p => p.IsHotSync)
            ?? throw new UsbOpenException("接続情報", new IOException("HotSync 以外の用途でつながっています"), isNonHotSync: true);
        if (port.InEndpoint is < 1 or > 15 || port.OutEndpoint is < 1 or > 15)
            throw new IOException("Connection info: invalid HotSync endpoint");
        return port;
    }
}
