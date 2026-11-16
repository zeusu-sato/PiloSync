using PiloSync.Transport;
using PiloSync.Protocol;
using Xunit;
using static PiloSync.Core.Tests.DlpFixtures;
namespace PiloSync.Core.Tests;

public class UsbByteChannelTests
{
    private sealed class BulkChannel : IByteChannel
    {
        public int Calls { get; private set; }
        private int offset;
        public bool Disposed { get; private set; }
        public string Name => "Fake USB";
        public bool IsSerial => false;
        public bool Disconnect { get; set; }
        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (Disconnect) throw new IOException("Removed");
            Calls++;
            int n = Math.Min(buffer.Length, 100 - offset);
            for (int i = 0; i < n; i++) buffer[i] = (byte)(offset + i);
            offset += n;
            return n;
        }
        public void Write(ReadOnlySpan<byte> data) { }
        public void SetBaudRate(int baud) => throw new InvalidOperationException();
        public void Dispose() => Disposed = true;
    }
    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public void UsbBulkReadPreservesRemainder(int size)
    {
        var lower = new BulkChannel();
        using (var channel = new PiloSync.Usb.UsbByteChannel(lower))
        {
            var received = new List<byte>(); var chunk = new byte[size];
            while (received.Count < 100)
            {
                int n = channel.Read(chunk, 100); Assert.InRange(n, 1, size); received.AddRange(chunk[..n]);
            }
            Assert.Equal(Enumerable.Range(0, 100).Select(i => (byte)i), received);
            Assert.True(lower.Calls >= 2); Assert.False(channel.IsSerial); channel.SetBaudRate(9600);
            Assert.Equal(0, channel.Read(chunk, 0));
            lower.Disconnect = true; Assert.Throws<IOException>(() => channel.Read(chunk, 100));
        }
        Assert.True(lower.Disposed);
    }


    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(65536)]
    public void NetReassemblesSplitAndCombinedPacketsWithTickle(int chunk)
    {
        var payload = Enumerable.Range(0, 192).Select(i => (byte)i).ToArray();
        var bytes = HandshakeBytes().Concat(NetCodec.Header(2, 255, 0)).Concat(NetBytes(1, payload)).Concat(NetBytes(2, [42])).ToArray();
        using var lower = new ChunkChannel(bytes, chunk);
        using var usb = new PiloSync.Usb.UsbByteChannel(lower);
        var net = new NetTransport(usb); net.AcceptHandshake();
        net.Send([8]); Assert.Equal(payload, net.Receive()); net.Send([9]); Assert.Equal(new byte[] { 42 }, net.Receive());
        Assert.Equal(Convert.FromHexString("01010000000108"), lower.Writes[2]);
        Assert.Equal(4, lower.Writes.Count);
    }
    [Theory]
    [InlineData(64, 65536)]
    [InlineData(4096, 65536)]
    [InlineData(65536, 65536)]
    [InlineData(131072, 65536)]
    [InlineData(4096, 37)]
    public void UsbNetReadsKnownBodyLengthWithoutWaitingForShortPacket(int size, int chunk)
    {
        var payload = Enumerable.Range(0, size).Select(i => (byte)i).ToArray();
        using var lower = new ChunkChannel(NetBytes(1, payload).Concat(NetBytes(1, [42])).ToArray(), chunk);
        using var usb = new PiloSync.Usb.UsbByteChannel(lower);
        var net = new NetTransport(usb, timeoutMs: 1000);
        Assert.Equal(payload, net.Receive());
        Assert.Equal(new byte[] { 42 }, net.Receive());
        Assert.All(lower.ReadSizes, n => Assert.Equal(64, n));
    }

    [Theory]
    [InlineData(1, 64)]
    [InlineData(64, 64)]
    [InlineData(65, 128)]
    [InlineData(512, 512)]
    [InlineData(65535, 65536)]
    public void UsbReadLimitRoundsUp(int value, int expected) =>
        Assert.Equal(expected, PiloSync.Usb.UsbByteChannel.NormalizeReadLimit(value));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65537)]
    [InlineData(int.MaxValue)]
    public void UsbReadLimitRejectsInvalidValues(int value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PiloSync.Usb.UsbByteChannel.NormalizeReadLimit(value));

    [Theory]
    [InlineData(64, 129, 64)]
    [InlineData(512, 65, 128)]
    [InlineData(513, 1000, 576)]
    [InlineData(1024, 2000, 1024)]
    [InlineData(4096, 4097, 4096)]
    [InlineData(65536, 65537, 65536)]
    public void UsbReadRequestUsesRoundedRemainderAndLimit(int limit, int remaining, int expected)
    {
        var input = Enumerable.Range(0, 70000).Select(i => (byte)i).ToArray();
        using var lower = new ChunkChannel(input, 65536);
        using var usb = new PiloSync.Usb.UsbByteChannel(lower, limit);
        var buffer = new byte[remaining];
        Assert.Equal(Math.Min(expected, remaining), usb.Read(buffer, 1000));
        Assert.Equal(expected, lower.ReadSizes[0]);
        if (expected > remaining)
        {
            var rest = new byte[expected - remaining];
            Assert.Equal(rest.Length, usb.Read(rest, 1000));
            Assert.Equal(input[remaining..expected], rest);
            Assert.Single(lower.ReadSizes);
        }
    }

    private sealed class FailingUsbChannel(int error, bool stall, bool timeout = false) : IByteChannel
    {
        public List<int> Sizes { get; } = new();
        public List<int> Timeouts { get; } = new();
        public string Name => "Failing USB";
        public bool IsSerial => false;
        public int Read(Span<byte> buffer, int timeoutMs)
        {
            Sizes.Add(buffer.Length); Timeouts.Add(timeoutMs);
            if (Sizes.Count == 1 || stall)
                throw new IOException("ReadFile", new System.ComponentModel.Win32Exception(error));
            if (timeout) return 0;
            buffer.Fill(42); return buffer.Length;
        }
        public void Write(ReadOnlySpan<byte> data) { }
        public void SetBaudRate(int baud) { }
        public void Dispose() { }
    }

    [Theory]
    [InlineData(31)]
    [InlineData(87)]
    public void UsbReadFallsBackOnceForConnection(int error)
    {
        using var lower = new FailingUsbChannel(error, false);
        var logs = new List<string>();
        using var usb = new PiloSync.Usb.UsbByteChannel(lower, 512, logs.Add);
        var buffer = new byte[1024];
        Assert.Equal(64, usb.Read(buffer, 1000));
        Assert.Equal(64, usb.Read(buffer, 1000));
        Assert.Equal(new[] { 512, 64, 64 }, lower.Sizes);
        Assert.Equal(5000, lower.Timeouts[1]);
        Assert.Single(logs);
    }

    [Theory]
    [InlineData(31, 512, true, 2)]
    [InlineData(87, 512, true, 2)]
    [InlineData(1167, 512, false, 1)]
    [InlineData(31, 64, false, 1)]
    public void UsbReadStopsAfterFallbackFailureOrRemoval(int error, int limit, bool stall, int calls)
    {
        using var lower = new FailingUsbChannel(error, stall);
        using var usb = new PiloSync.Usb.UsbByteChannel(lower, limit);
        Assert.Throws<IOException>(() => usb.Read(new byte[1024], 1000));
        Assert.Throws<IOException>(() => usb.Read(new byte[1024], 1000));
        Assert.Equal(calls, lower.Sizes.Count);
    }

    [Fact]
    public void UsbReadStopsWhenFallbackTimesOut()
    {
        using var lower = new FailingUsbChannel(31, false, timeout: true);
        using var usb = new PiloSync.Usb.UsbByteChannel(lower, 512);
        Assert.Throws<IOException>(() => usb.Read(new byte[1024], 1000));
        Assert.Throws<IOException>(() => usb.Read(new byte[1024], 1000));
        Assert.Equal(new[] { 512, 64 }, lower.Sizes);
        Assert.Equal(5000, lower.Timeouts[1]);
    }

    private sealed class DelayedBulkChannel(byte[] bytes) : IByteChannel
    {
        public readonly ManualResetEventSlim Started = new();
        public readonly ManualResetEventSlim Release = new();
        public int Requested;
        public string Name => "Delayed USB";
        public bool IsSerial => false;
        public int Read(Span<byte> buffer, int timeoutMs)
        {
            Requested = buffer.Length;
            Started.Set(); Release.Wait();
            bytes.CopyTo(buffer); return bytes.Length;
        }
        public void Write(ReadOnlySpan<byte> data) { }
        public void SetBaudRate(int baud) { }
        public void Dispose() => Release.Set();
    }
    [Fact]
    public void UsbPendingReadSurvivesTimeoutAndPreservesCombinedPackets()
    {
        using var lower = new DelayedBulkChannel(NetBytes(1, [7]).Concat(NetBytes(1, [8])).ToArray());
        using var usb = new PiloSync.Usb.UsbByteChannel(lower);
        Assert.Equal(0, usb.Read(new byte[128], 50));
        Assert.True(lower.Started.Wait(1000));
        lower.Release.Set();
        var net = new NetTransport(usb, timeoutMs: 1000);
        Assert.Equal(new byte[] { 7 }, net.Receive());
        Assert.Equal(new byte[] { 8 }, net.Receive());
        Assert.Equal(64, lower.Requested);
    }
    [Fact]
    public async Task UsbIdleCancellationUnblocksPendingRead()
    {
        var (host, device) = MemoryChannel.Pair(); using var peer = device;
        using var usb = new PiloSync.Usb.UsbByteChannel(host);
        using var stop = new CancellationTokenSource(50);
        await Task.Run(() => Assert.Throws<OperationCanceledException>(() =>
            PalmConnection.Accept(usb, cancellation: stop.Token))).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(host.Disposed);
    }

}
