using System.Buffers.Binary;
using PiloSync.Transport;
using PiloSync.Protocol;
using Xunit;
using static PiloSync.Core.Tests.DlpFixtures;
namespace PiloSync.Core.Tests;

public class ConnectionTests
{
    [Fact]
    public async Task FragmentedTransportReassemblesAndRetriesLostAck()
    {
        var (h, d) = MemoryChannel.Pair(); using var host = h; using var device = d;
        int dropped = 0; d.DropWrite = b => b.Length >= 14 && b[10] == 2 && Interlocked.Increment(ref dropped) == 1;
        var payload = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
        var receiver = Task.Run(() => { var t = new PadpTransport(d, timeoutMs: 1000); var got = t.Receive(); t.Send([42]); return got; });
        var sender = Task.Run(() => { var t = new PadpTransport(h, timeoutMs: 100); t.Send(payload); Assert.Equal(new byte[] { 42 }, t.Receive()); });
        await sender.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(payload, await receiver.WaitAsync(TimeSpan.FromSeconds(5)));
    }
    [Theory]
    [InlineData(57600, 115200, 57600)]
    [InlineData(115200, 57600, 57600)]
    [InlineData(115200, 115200, 115200)]
    [InlineData(57600, 9600, 9600)]
    public async Task CmpDlpSession(int requested, int maximum, int expected)
    {
        var (h, d) = MemoryChannel.Pair(); using var host = h; using var device = d;
        var fake = Task.Run(() =>
        {
            var t = new PadpTransport(d, timeoutMs: 1000); byte[] wake = [1, 0, 1, 2, 0, 0, 0, 0, 0, 0]; BinaryPrimitives.WriteUInt32BigEndian(wake.AsSpan(6), (uint)maximum); t.Send(wake);
            var init = t.Receive(); Assert.Equal(2, init[0]); Assert.Equal((uint)expected, BinaryPrimitives.ReadUInt32BigEndian(init.AsSpan(6))); Assert.Equal(expected == 9600 ? 0 : 0x80, init[1]); t.Transaction = 1;
            foreach (byte command in new byte[] { 0x12, 0x10, 0x2e, 0x16, 0x16, 0x2f })
            {
                var req = t.Receive(); Assert.Equal(command, req[0]); byte[]? response = null;
                if (command == 0x12) response = Convert.FromHexString("03503000000000000000");
                if (command == 0x10) response = User();
                if (command == 0x16) { ushort index = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(6)); response = DbPage(index == 0, index); }
                if (command == 0x2f) Assert.Equal(new byte[] { 0, 0 }, req[^2..]);
                t.Send(Reply(command, response)); t.AdvanceTransaction();
            }
        });
        var run = Task.Run(() =>
        {
            using var connection = PalmConnection.Accept(h, requested, timeoutMs: 1000); Assert.Equal("SLP", connection.Protocol); Assert.Equal(expected, h.Baud);
            Assert.Equal("3.5.0", connection.Dlp.ReadSysInfo().OsVersion); Assert.Equal("IIIc", connection.Dlp.ReadUserInfo().Name);
            connection.Dlp.OpenConduit(); Assert.Equal(2, connection.Dlp.ReadDBList().Count); connection.Dlp.EndOfSync();
        });
        await Task.WhenAll(run, fake).WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(h.Disposed);
    }
    [Fact]
    public async Task SerialSkipsNoiseBeforeCmpWakeup()
    {
        var (h, d) = MemoryChannel.Pair(); using var host = h; using var device = d;
        var fake = Task.Run(() =>
        {
            d.Write([0xff, 0x00]);
            var t = new PadpTransport(d, timeoutMs: 1000);
            t.Send([1, 0, 1, 2, 0, 0, 0, 0, 0xe1, 0]);
            Assert.Equal(2, t.Receive()[0]);
        });
        var accept = Task.Run(() => { using var c = PalmConnection.Accept(h, timeoutMs: 1000); Assert.Equal("SLP", c.Protocol); Assert.Equal(2, c.SkippedNoiseBytes); });
        await Task.WhenAll(fake, accept).WaitAsync(TimeSpan.FromSeconds(5));
    }
    [Fact]
    public void NonSerialRejectsUnknownFirstByte()
    {
        using var channel = new ChunkChannel([0xff], 1);
        Assert.Equal("Unknown protocol first byte 0xFF", Assert.Throws<ProtocolException>(() => PalmConnection.Accept(channel)).Message);
    }
    [Fact]
    public async Task MidPacketDisconnectFailsWithoutHanging()
    {
        var (h, d) = MemoryChannel.Pair(); using var host = h; using var device = d;
        d.Write([0xbe, 0xef, 0xed, 3]); d.Dispose();
        await Task.Run(() => Assert.Throws<IOException>(() => PalmConnection.Accept(h, timeoutMs: 200))).WaitAsync(TimeSpan.FromSeconds(2)); Assert.True(h.Disposed);
    }
    [Fact]
    public async Task MidDlpDisconnectDisposesSession()
    {
        var (h, d) = MemoryChannel.Pair(); using var host = h; using var device = d;
        var fake = Task.Run(() => { var t = new PadpTransport(d, timeoutMs: 300); t.Send([1, 0, 1, 2, 0, 0, 0, 0, 0xe1, 0]); t.Receive(); t.Transaction = 1; t.Receive(); d.Dispose(); });
        var run = Task.Run(() => Assert.Throws<IOException>(() => { using var c = PalmConnection.Accept(h, timeoutMs: 300); c.Dlp.ReadUserInfo(); }));
        await Task.WhenAll(run, fake).WaitAsync(TimeSpan.FromSeconds(3)); Assert.True(h.Disposed);
    }
    [Fact]
    public async Task PartialPacketTimeoutDisposesChannel()
    {
        var (h, d) = MemoryChannel.Pair(); using var host = h; using var device = d; d.Write([0xbe]);
        await Task.Run(() => Assert.Throws<TimeoutException>(() => PalmConnection.Accept(h, timeoutMs: 50))).WaitAsync(TimeSpan.FromSeconds(2)); Assert.True(h.Disposed);
    }
    [Fact]
    public async Task IdleCancellationDisposesChannel()
    {
        var (h, d) = MemoryChannel.Pair(); using var host = h; using var device = d; using var stop = new CancellationTokenSource(50);
        await Task.Run(() => Assert.Throws<OperationCanceledException>(() => PalmConnection.Accept(h, cancellation: stop.Token))).WaitAsync(TimeSpan.FromSeconds(2)); Assert.True(h.Disposed);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetThreeStageHandshakeAndDlp(bool headerless)
    {
        var (h, d) = MemoryChannel.Pair(); using var host = h; using var device = d;
        var fake = Task.Run(() =>
        {
            byte[] Read(int n) { var b = new byte[n]; for (int i = 0; i < n; i++) if (d.Read(b.AsSpan(i, 1), 1000) != 1) throw new TimeoutException(); return b; }
            void Send(byte[] b) { d.Write(NetCodec.Header(1, 1, b.Length)); d.Write(b); }
            byte[] Receive() { var head = Read(6); return Read((int)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(2))); }
            var hello = new byte[22]; hello[0] = 0x90; if (headerless) d.Write(hello); else Send(hello);
            var first = Receive(); Assert.Equal(50, first.Length); Assert.Equal(0x12, first[0]);
            var middle = new byte[50]; middle[0] = 0x92; Send(middle); var second = Receive(); Assert.Equal(46, second.Length); Assert.Equal(0x13, second[0]);
            Send([0x93, 0, 0, 0, 0, 0, 0, 0]); Assert.Equal(new byte[] { 0x10, 0 }, Receive()); Send(Reply(0x10, User()));
        });
        var run = Task.Run(() => { using var c = PalmConnection.Accept(h, timeoutMs: 1000); Assert.Equal("NET", c.Protocol); Assert.Equal("IIIc", c.Dlp.ReadUserInfo().Name); });
        await Task.WhenAll(fake, run).WaitAsync(TimeSpan.FromSeconds(5));
    }
    [Fact]
    public async Task NetDeleteDbWaitsBeyondDefaultTimeout()
    {
        var (h, d) = MemoryChannel.Pair(); using var host = h; using var device = d;
        var reply = Task.Run(() => { Thread.Sleep(150); d.Write(NetCodec.Header(1, 0, 4)); d.Write([0x9a, 0, 0, 0]); });
        await Task.Run(() => new DlpClient(new NetTransport(h, timeoutMs: 50)).DeleteDB("test")).WaitAsync(TimeSpan.FromSeconds(2));
        await reply;
    }
    [Fact]
    public async Task NetOrdinaryRequestTimesOut()
    {
        var (h, d) = MemoryChannel.Pair(); using var host = h; using var device = d;
        await Task.Run(() => Assert.Throws<TimeoutException>(() => new DlpClient(new NetTransport(h, timeoutMs: 50)).OpenConduit())).WaitAsync(TimeSpan.FromSeconds(2));
    }
    [Fact]
    public async Task NetTickleExtendsDeadline()
    {
        var (h, d) = MemoryChannel.Pair(); using var host = h; using var device = d;
        var reply = Task.Run(() => { Thread.Sleep(100); d.Write(NetCodec.Header(2, 0, 0)); Thread.Sleep(100); d.Write(NetCodec.Header(1, 0, 1)); d.Write([42]); });
        Assert.Equal(new byte[] { 42 }, await Task.Run(() => new NetTransport(h, timeoutMs: 170).Receive()).WaitAsync(TimeSpan.FromSeconds(2)));
        await reply;
    }
    [Fact]
    public async Task NetTicklesStopAtMaximumDeadline()
    {
        var (h, d) = MemoryChannel.Pair(); using var host = h; using var device = d; using var stop = new CancellationTokenSource();
        var tickles = Task.Run(() => { while (!stop.IsCancellationRequested) { try { d.Write(NetCodec.Header(2, 0, 0)); } catch (IOException) { break; } Thread.Sleep(5); } });
        var net = new NetTransport(h, timeoutMs: 80); typeof(NetTransport).GetProperty("MaxReceiveTimeoutMs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(net, 200);
        try { await Task.Run(() => Assert.Throws<TimeoutException>(() => net.Receive())).WaitAsync(TimeSpan.FromSeconds(2)); }
        finally { stop.Cancel(); await tickles; }
    }
    [Fact]
    public void NetInvalidHeaderIncludesHex()
    {
        var (h, d) = MemoryChannel.Pair(); using var host = h; using var device = d;
        d.Write(Convert.FromHexString("0A0100000123"));
        Assert.Contains("0A0100000123", Assert.Throws<ProtocolException>(() => new NetTransport(h).Receive()).Message);
    }
    [Theory]
    [InlineData("0102", "0102")]
    [InlineData("", "none")]
    public void NetInvalidHeaderIncludesContext(string following, string expectedFollowing)
    {
        var (h, d) = MemoryChannel.Pair(); using var host = h; using var device = d;
        var net = new NetTransport(h, timeoutMs: 1000);
        d.Write(NetCodec.Header(1, 0xcd, 4)); d.Write([1, 2, 3, 4]);
        net.Receive(); net.Send([1, 2, 3]);
        d.Write(Convert.FromHexString("28CD00000000" + following));
        string message = Assert.Throws<ProtocolException>(() => net.Receive()).Message;
        Assert.StartsWith("NET header type/length: 28CD00000000", message);
        Assert.Contains("expected tx CD; last sent len 3 tx CD; last recv tx CD len 4", message);
        Assert.EndsWith("following: " + expectedFollowing, message);
    }
    [Fact]
    public async Task WrongSequenceTerminatesSession()
    {
        var (h, d) = MemoryChannel.Pair(); using var host = h; using var device = d;
        d.Write(SlpCodec.Encode(new(3, 3, 2, 9, PadpCodec.Encode(new(1, 0xc0, 1, [4])))));
        await Task.Run(() => Assert.Throws<ProtocolException>(() => new PadpTransport(h, timeoutMs: 100) { Transaction = 0xff }.Receive())).WaitAsync(TimeSpan.FromSeconds(2));
    }
    [Fact]
    public async Task TickleDoesNotExtendAbsoluteTimeout()
    {
        var (h, d) = MemoryChannel.Pair(); using var host = h; using var device = d;
        using var stop = new CancellationTokenSource();
        var tickles = Task.Run(() => { while (!stop.IsCancellationRequested) { try { d.Write(SlpCodec.Encode(new(3, 3, 2, 255, PadpCodec.Encode(new(4, 0, 0, []))))); } catch (IOException) { break; } Thread.Sleep(5); } });
        try { await Task.Run(() => Assert.Throws<TimeoutException>(() => new PadpTransport(h, timeoutMs: 80).Receive())).WaitAsync(TimeSpan.FromSeconds(2)); }
        finally { stop.Cancel(); await tickles; }
    }
    [Fact]
    public async Task MissingAckExhaustsRetries()
    {
        var (h, d) = MemoryChannel.Pair(); using var host = h; using var device = d;
        int writes = 0; h.DropWrite = _ => { Interlocked.Increment(ref writes); return true; };
        await Task.Run(() => Assert.Throws<TimeoutException>(() => new PadpTransport(h, timeoutMs: 20) { RetryCount = 3 }.Send([1]))).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(3, writes);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x37)]
    [InlineData(0xff)]
    public async Task CmpAdoptsWakeupTransaction(byte transaction)
    {
        var (h, d) = MemoryChannel.Pair(); using var host = h; using var device = d;
        var fake = Task.Run(() =>
        {
            // Send waits for an ACK with this exact transaction; Receive also checks INIT.
            var t = new PadpTransport(d, timeoutMs: 500) { Transaction = transaction };
            t.Send([1, 0, 1, 2, 0, 0, 0, 0, 0xe1, 0]);
            Assert.Equal(2, t.Receive()[0]); Assert.Equal(transaction, t.Transaction);
        });
        var accept = Task.Run(() => { using var c = PalmConnection.Accept(h, timeoutMs: 500); Assert.Equal("SLP", c.Protocol); });
        await Task.WhenAll(fake, accept).WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void NetTransactionIncludesFeAndSkipsFf()
    {
        using var channel = new ChunkChannel(HandshakeBytes(0xfd).Concat(NetBytes(0xfd, [1])).Concat(NetBytes(0xfe, [2])).Concat(NetBytes(1, [3])).ToArray(), 99);
        var net = new NetTransport(channel); net.AcceptHandshake();
        foreach (byte expected in new byte[] { 0xfd, 0xfe, 1 })
        { net.Send([7]); net.Receive(); Assert.Equal(expected, channel.Writes[^1][1]); }
    }
    [Fact]
    public void TracingShowsTransfersAndOmitsEmptyRead()
    {
        using var lower = new ChunkChannel([1, 5], 64); var lines = new List<string>();
        using var trace = new TracingChannel(lower, lines.Add);
        trace.Write([1, 5, 0]); var buffer = new byte[8]; Assert.Equal(2, trace.Read(buffer, 0)); Assert.Equal(0, trace.Read(buffer, 0));
        Assert.Equal(2, lines.Count);
        Assert.Matches(@"^\d{2}:\d{2}:\d{2}\.\d{3} OUT 3 bytes: 01 05 00$", lines[0]);
        Assert.EndsWith("IN 2 bytes: 01 05", lines[1]);
    }
    [Fact]
    public void AbortSendsOtherCodeAndAcceptsDifferentTransaction()
    {
        using var lower = new ChunkChannel(HandshakeBytes().Concat(NetBytes(99, Reply(0x2f))).ToArray(), 9);
        using var connection = PalmConnection.Accept(lower); connection.TryAbort();
        Assert.Equal(Convert.FromHexString("0101000000062F0120020003"), lower.Writes[^1]);
    }
}
