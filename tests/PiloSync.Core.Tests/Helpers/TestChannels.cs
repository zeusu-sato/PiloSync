using PiloSync.Protocol;
using System.Collections.Concurrent;
using PiloSync.Transport;
namespace PiloSync.Core.Tests;

// No physical ports: the pair transfers bytes through in-memory queues only.
internal sealed class MemoryChannel : IByteChannel
{
    private readonly BlockingCollection<byte> input = new();
    private MemoryChannel peer = null!;
    private int closed;
    public bool Disposed => closed != 0;
    public string Name => "Memory";
    public bool IsSerial => true;
    public int Baud { get; private set; } = 9600;
    public Func<byte[], bool>? DropWrite { get; set; }
    public static (MemoryChannel Host, MemoryChannel Device) Pair()
    { var h = new MemoryChannel(); var d = new MemoryChannel(); h.peer = d; d.peer = h; return (h, d); }
    public int Read(Span<byte> buffer, int timeoutMs)
    {
        if (Disposed) throw new IOException("Local disconnect");
        if (buffer.IsEmpty) return 0;
        if (!input.TryTake(out byte b, timeoutMs))
        { if (input.IsCompleted) throw new IOException("Peer disconnect"); return 0; }
        buffer[0] = b; return 1; // Exercise partial reads on every packet.
    }
    public void Write(ReadOnlySpan<byte> data)
    {
        if (Disposed || peer.Disposed) throw new IOException("Disconnect");
        byte[] bytes = data.ToArray(); if (DropWrite?.Invoke(bytes) == true) return;
        try { foreach (byte b in bytes) peer.input.Add(b); } catch (InvalidOperationException ex) { throw new IOException("Disconnect", ex); }
    }
    public void SetBaudRate(int baud) => Baud = baud;
    public void Dispose() { if (Interlocked.Exchange(ref closed, 1) == 0) { input.CompleteAdding(); peer.input.CompleteAdding(); } }
}


internal sealed class ChunkChannel(byte[] input, int chunk) : IByteChannel
{
    private int offset;
    public List<int> ReadSizes { get; } = new();
    public List<byte[]> Writes { get; } = new();
    public string Name => "USB test";
    public bool IsSerial => false;
    public int Read(Span<byte> buffer, int timeoutMs)
    {
        ReadSizes.Add(buffer.Length);
        int n = Math.Min(Math.Min(chunk, buffer.Length), input.Length - offset);
        input.AsSpan(offset, n).CopyTo(buffer); offset += n; return n;
    }
    public void Write(ReadOnlySpan<byte> data) => Writes.Add(data.ToArray());
    public void SetBaudRate(int baud) { }
    public void Dispose() { }
}

internal sealed class Fake(Func<byte[], byte[]> reply) : IPacketTransport
{
    public List<byte[]> Requests { get; } = [];
    private byte[] last = [];
    public void Send(ReadOnlySpan<byte> b) { last = b.ToArray(); Requests.Add(last); }
    public byte[] Receive() => reply(last);
}
internal sealed class Folder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PiloSync-tests-" + Guid.NewGuid().ToString("N"));
    public Folder() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path,true);
}
