using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PiloSync.Serial;
using Xunit;

namespace PiloSync.Core.Tests;

public class SerialTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RecoveredHandshakeNoiseGoesOnlyToTrace(bool tracing)
    {
        var normal = new List<string>(); var trace = new List<string>();
        var messages = new SerialHandshakeLog(normal.Add, tracing ? trace.Add : null);
        string[] noise = ["SetCommState(9600,minimal): Win32=31", "失敗: Unknown protocol first byte 0x00", "失敗: SLP: unexpected socket/type", "復帰"];
        foreach (string message in noise) messages.Failure(message);
        messages.Accepted(); messages.Flush();
        Assert.Empty(normal);
        Assert.Equal(tracing ? noise : [], trace);
    }
    [Fact]
    public void UnrecoveredHandshakeNoiseExpiresFromFirstFailure()
    {
        var normal = new List<string>(); var trace = new List<string>();
        var now = TimeSpan.Zero;
        var messages = new SerialHandshakeLog(normal.Add, trace.Add, () => now, TimeSpan.FromSeconds(2));
        messages.Failure("失敗: Unknown protocol first byte 0x00");
        now = TimeSpan.FromSeconds(1);
        messages.BeginAttempt(); messages.Failure("失敗: SLP: unexpected socket/type");
        messages.CheckExpired(); Assert.Empty(normal);
        now = TimeSpan.FromSeconds(2); messages.CheckExpired();
        messages.Failure("失敗: ReadFile: Win32=31");
        messages.Accepted(); messages.Flush();
        Assert.Equal(new[] { "失敗: Unknown protocol first byte 0x00", "失敗: SLP: unexpected socket/type" }, normal);
        Assert.Equal(new[] { "失敗: ReadFile: Win32=31" }, trace);
    }
    [Fact]
    public void NewFailureLongAfterExpirationIsBufferedUntilAccepted()
    {
        var normal = new List<string>(); var trace = new List<string>();
        var now = TimeSpan.Zero;
        var messages = new SerialHandshakeLog(normal.Add, trace.Add, () => now);
        messages.Failure("失敗: old");
        now = TimeSpan.FromSeconds(31); messages.CheckExpired();
        now = TimeSpan.FromHours(3); messages.Failure("失敗: new");
        messages.Failure("復帰"); messages.Accepted();
        Assert.Equal(new[] { "失敗: old" }, normal);
        Assert.Equal(new[] { "失敗: new", "復帰" }, trace);
    }
    [Fact]
    public void RecoveryAfterExpirationIsLoggedNormally()
    {
        var normal = new List<string>(); var trace = new List<string>();
        var now = TimeSpan.Zero;
        var messages = new SerialHandshakeLog(normal.Add, trace.Add, () => now);
        messages.Failure("失敗: old");
        now = TimeSpan.FromSeconds(31); messages.CheckExpired();
        messages.Failure("復帰"); messages.Accepted();
        Assert.Equal(new[] { "失敗: old", "復帰" }, normal);
        Assert.Empty(trace);
    }
    [Fact]
    public void PendingHandshakeNoiseIsFlushedOnStopOrRemoval()
    {
        var normal = new List<string>();
        var messages = new SerialHandshakeLog(normal.Add);
        messages.Failure("失敗: Unknown protocol first byte 0x00");
        messages.Flush(); messages.Flush();
        Assert.Equal(new[] { "失敗: Unknown protocol first byte 0x00" }, normal);
    }
    [Fact]
    public void SessionFailuresAreImmediateAndNextHandshakeIsBuffered()
    {
        var normal = new List<string>(); var trace = new List<string>();
        var messages = new SerialHandshakeLog(normal.Add, trace.Add);
        messages.Accepted(); messages.Failure("失敗: session error");
        Assert.Equal(new[] { "失敗: session error" }, normal);
        messages.BeginAttempt(); messages.Failure("SetCommState: Win32=31");
        Assert.Single(normal);
        messages.Accepted(); Assert.Equal(new[] { "SetCommState: Win32=31" }, trace);
    }
    [Fact]
    public void CompatibilityInformationIsImmediateDuringHandshake()
    {
        var normal = new List<string>();
        var messages = new SerialHandshakeLog(normal.Add);
        messages.Failure("ReadFile: Win32=31");
        string[] information = ["互換設定を試行: no-dcb", "互換設定を記憶: no-dcb", "互換設定の読み込み: error", "互換設定の保存: error"];
        foreach (string message in information) messages.Information(message);
        Assert.Equal(information, normal);
        messages.Accepted(); Assert.Equal(information, normal);
    }
    [Fact]
    public void LateHandshakeSuccessDoesNotHideExpiredNoise()
    {
        var normal = new List<string>(); var trace = new List<string>();
        var now = TimeSpan.Zero;
        var messages = new SerialHandshakeLog(normal.Add, trace.Add, () => now);
        messages.Failure("失敗: Unknown protocol first byte 0x00");
        now = TimeSpan.FromSeconds(30); messages.Accepted();
        Assert.Single(normal); Assert.Empty(trace);
    }
    [Fact]
    public void ListeningMovesSettingsToTraceWithoutStartingTimer()
    {
        var normal = new List<string>(); var trace = new List<string>(); var now = TimeSpan.Zero;
        var messages = new SerialHandshakeLog(normal.Add, trace.Add, () => now);
        messages.Setting("SetCommState: Win32=31"); messages.Listening();
        now = TimeSpan.FromHours(3); messages.CheckExpired(); messages.Flush();
        Assert.Empty(normal); Assert.Equal(new[] { "SetCommState: Win32=31" }, trace);
    }
    [Fact]
    public void SettingsWaitForFailureBeforeStartingTimer()
    {
        var normal = new List<string>(); var now = TimeSpan.Zero;
        var messages = new SerialHandshakeLog(normal.Add, clock: () => now);
        messages.Setting("SetCommState: Win32=31");
        now = TimeSpan.FromHours(3); messages.CheckExpired(); Assert.Empty(normal);
        messages.Failure("失敗: ReadFile");
        now += TimeSpan.FromSeconds(29); messages.CheckExpired(); Assert.Empty(normal);
        now += TimeSpan.FromSeconds(1); messages.CheckExpired();
        Assert.Equal(new[] { "SetCommState: Win32=31", "失敗: ReadFile" }, normal);
    }
    [Fact]
    public void OpenFailureExpiresAfterThirtySeconds()
    {
        var normal = new List<string>(); var now = TimeSpan.Zero;
        var messages = new SerialHandshakeLog(normal.Add, clock: () => now);
        messages.Failure("失敗: open");
        now = TimeSpan.FromSeconds(30); messages.CheckExpired();
        Assert.Equal(new[] { "失敗: open" }, normal);
    }
    [Fact]
    public void ListeningPreservesExpiredFailureForRecovery()
    {
        var normal = new List<string>(); var trace = new List<string>(); var now = TimeSpan.Zero;
        var messages = new SerialHandshakeLog(normal.Add, trace.Add, () => now);
        messages.Failure("失敗: old"); now = TimeSpan.FromSeconds(31); messages.CheckExpired();
        messages.BeginAttempt(); messages.Setting("SetCommState: Win32=31"); messages.Listening();
        messages.Failure("復帰"); messages.Accepted();
        Assert.Equal(new[] { "失敗: old", "復帰" }, normal);
        Assert.Equal(new[] { "SetCommState: Win32=31" }, trace);
    }
    [Fact]
    public async Task ListeningIsNotifiedOnlyOnceAfterSuccessfulReads()
    {
        string name = "PiloSync-test-" + Guid.NewGuid();
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync(); int calls = 0;
        using var channel = new SerialChannel(@"pipe\" + name, null, 5, null, () => calls++);
        await connected;
        var buffer = new byte[8];
        Assert.Equal(0, channel.Read(buffer, 30)); Assert.Equal(1, calls);
        var sending = server.WriteAsync(new byte[] { 42 });
        Assert.Equal(1, channel.Read(buffer, 2000)); await sending;
        Assert.Equal(1, calls);
    }
    [Fact]
    public void DcbHasWin32Layout()
    {
        Assert.Equal(28, Marshal.SizeOf<SerialNative.Dcb>());
        Assert.Equal(8, Marshal.OffsetOf<SerialNative.Dcb>("Flags").ToInt32());
        Assert.Equal(18, Marshal.OffsetOf<SerialNative.Dcb>("ByteSize").ToInt32());
        Assert.Equal(21, Marshal.OffsetOf<SerialNative.Dcb>("XonChar").ToInt32());
    }
    [Theory]
    [InlineData(9600)]
    [InlineData(57600)]
    public void MinimalDcbClearsDriverState(int baud)
    {
        var d = SerialNative.Dcb.Empty;
        d.Length = 0; d.Flags = uint.MaxValue;
        d.Reserved = d.Reserved1 = ushort.MaxValue;
        d.ByteSize = d.Parity = d.StopBits = byte.MaxValue;
        d.XonChar = d.XoffChar = d.ErrorChar = d.EofChar = d.EvtChar = 63;
        d.XonLim = 34412; d.XoffLim = 8603;
        d.Minimal(baud);
        Assert.Equal(28u, d.Length);
        Assert.Equal(0x1011u, d.Flags); // Binary, DTR enabled, RTS enabled; all flow control disabled.
        Assert.Equal((uint)baud, d.BaudRate);
        Assert.Equal(8, d.ByteSize); Assert.Equal(0, d.Parity); Assert.Equal(0, d.StopBits);
        Assert.Equal(17, d.XonChar); Assert.Equal(19, d.XoffChar);
        Assert.Equal(0, d.ErrorChar); Assert.Equal(0, d.EofChar); Assert.Equal(0, d.EvtChar);
        Assert.Equal(0, d.XonLim); Assert.Equal(0, d.XoffLim);
        Assert.Equal(0, d.Reserved); Assert.Equal(0, d.Reserved1);
        d.Minimal(baud == 9600 ? 57600 : 9600);
        Assert.Equal(0x1011u, d.Flags);
        Assert.Equal(0, d.XonLim); Assert.Equal(0, d.XoffLim);
    }
    [Fact]
    public void DcbLineControlsChangeOnlyTheirTwoBitFields()
    {
        var d = SerialNative.Dcb.Empty; d.Flags = uint.MaxValue; d.Lines();
        Assert.Equal(0xFFFFDFDFu, d.Flags);
        Assert.Equal(1u, (d.Flags >> 4) & 3); Assert.Equal(1u, (d.Flags >> 12) & 3);
        Assert.NotEqual(SerialNative.Dcb.BuildBase.XonChar, SerialNative.Dcb.BuildBase.XoffChar);
    }
    [Fact]
    public void RetrySuppressesIdenticalErrorsAndResetsAfterRecovery()
    {
        var r = new SerialRetry();
        Assert.True(r.Changed("ReadFile: Win32=31")); Assert.False(r.Changed("ReadFile: Win32=31"));
        Assert.True(r.Changed("SetCommState: Win32=31"));
        Assert.Equal(new[] { 500, 1000, 2000, 3000, 3000 }, Enumerable.Range(0, 5).Select(_ => r.NextDelay()));
        r.Opened(); Assert.Equal(500, r.NextDelay());
        Assert.False(r.Changed("SetCommState: Win32=31"));
        Assert.True(r.Recovered()); Assert.False(r.Recovered()); Assert.True(r.Changed("SetCommState: Win32=31"));
    }
    [Fact]
    public async Task UnconfirmedBaudRateGoesOnlyToFailure()
    {
        string name = "PiloSync-test-" + Guid.NewGuid();
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync();
        var normal = new List<string>(); var failures = new List<string>();
        using var channel = new SerialChannel(@"pipe\" + name, normal.Add, 5, failures.Add);
        await connected;
        channel.SetBaudRate(57600);
        Assert.Contains("SetBaudRate(57600): 速度変更を確認できません。通信を続行します", failures);
        Assert.Empty(normal);
    }
    [Fact]
    public async Task OverlappedReadTimeoutCancelsAndNextReadStillWorks()
    {
        string name = "PiloSync-test-" + Guid.NewGuid();
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync();
        using var h = SerialNative.Open(@"pipe\" + name);
        await connected;
        object gate = new(); var bytes = new byte[8];
        Assert.Equal(0, SerialNative.Transfer(h, gate, () => false, bytes, false, 30));
        var sending = server.WriteAsync(new byte[] { 42 });
        Assert.Equal(1, SerialNative.Transfer(h, gate, () => false, bytes, false, 2000)); Assert.Equal(42, bytes[0]);
        await sending;
    }
    [Fact]
    public async Task DisposeCancelsPendingReadWithoutReleasingOverlappedMemoryEarly()
    {
        string name = "PiloSync-test-" + Guid.NewGuid();
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync();
        using var h = SerialNative.Open(@"pipe\" + name);
        await connected;
        object gate = new(); bool disposed = false;
        var read = Task.Run(() => Assert.Throws<IOException>(() => SerialNative.Transfer(h, gate, () => disposed, new byte[8], false, 10000)));
        await Task.Delay(50);
        lock (gate) { disposed = true; SerialNative.CancelIoEx(h, IntPtr.Zero); h.Dispose(); }
        var error = await read.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains("Win32=995", error.Message);
    }
}
