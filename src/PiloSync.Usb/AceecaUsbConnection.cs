using Microsoft.Win32.SafeHandles;

namespace PiloSync.Usb;

// Reverse-engineered, read-only evidence in the installed AceecaUSBDx64.sys:
// 0x222000: output >= 20 bytes; cached extended response copied verbatim up to
// min(output length, 64). Input is not examined in the dispatch branch (VA 1467c).
// 0x222424: output >= 18 bytes; legacy vendor response (VA 148a3).
// 0x222008 GET_TIMEOUTS: output >= 8, int32 readMs then writeMs (VA 14ad3).
// 0x22200c SET_TIMEOUTS: input >= 8, same order; * -10000 converts to NT time;
// nonzero values < 100 are raised to 100 ms (VA 14c62). We do not modify these
// device-global settings; our timeouts use overlapped I/O and CancelIoEx.
// IN_nn/OUT_nn are decimal endpoint numbers (IN adds 0x80, OUT clears it,
// low nibble retained), not USB pipe indexes (VA 177b0). PIPE0..3 are a separate
// compatibility naming scheme and are intentionally not guessed here.
// oem117.inf:257/326 routes Visor to the same Aceeca driver as Palm.
// PipeWithName (VA 177b0; decimal parse 178c1, direction 1798b) is
// model-independent. Use the enumerated path from either registered GUID;
// there is no need to force the Handspring GUID for endpoint opens.
// SysWOW64/usbport.dll VA 66561b10 also tries extended then legacy info.
public sealed class AceecaUsbConnection : PiloSync.Transport.IByteChannel
{
    private SafeFileHandle? input, output;
    private readonly object readLock = new(), writeLock = new();
    private readonly Action<string>? log;
    private int disposed;
    private Native.RequestStorage? readStorage;
    private readonly byte[] readBuffer = new byte[65536];
    public string Name { get; }
    public bool IsSerial => false;
    public UsbConnectionInfo ConnectionInfo { get; private set; } = null!;
    public string InputPath { get; private set; } = "";
    public string OutputPath { get; private set; } = "";

    private AceecaUsbConnection(UsbDeviceInfo device, Action<string>? diagnostics)
    { Name = $"USB {device.ModelName} [{device.InstanceId}]"; log = diagnostics; }

    public static AceecaUsbConnection Open(UsbDeviceInfo device) => Open(device, null);

    // Called before pipe opens, so the probe can report connection information even
    // when an endpoint cannot be opened. No bytes are written to the handheld.
    // Keep per-I/O diagnostics enabled by default for UsbProbe.
    public static AceecaUsbConnection Open(UsbDeviceInfo device, Action<string>? diagnostics,
        Action<UsbConnectionInfo>? connectionInfoReceived = null, CancellationToken cancellation = default, bool logIo = true)
    {
        var connection = new AceecaUsbConnection(device, logIo ? diagnostics : null);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            using (var control = OpenStage(device.DevicePath, "CreateFile(制御)", diagnostics))
            {
                // Visor's request 0x04 can stall; request 0x03 first for Handspring.
                uint code = device.Vid == 0x082D ? 0x222424u : 0x222000u;
                byte[] bytes;
                try { bytes = RequestInfo(control, code, diagnostics, cancellation); }
                catch (UsbOpenException first) when (first.Win32Error is int error && error is not (2 or 3 or 6 or 995 or 1167))
                {
                    cancellation.ThrowIfCancellationRequested();
                    code = code == 0x222000 ? 0x222424u : 0x222000u;
                    try { bytes = RequestInfo(control, code, diagnostics, cancellation); }
                    catch (UsbOpenException second)
                    { throw new UsbOpenException(second.Stage, second, first); }
                }
                diagnostics?.Invoke($"接続情報 raw IOCTL=0x{code:X6}: {Convert.ToHexString(bytes)}");
                try
                {
                    connection.ConnectionInfo = new(code, bytes);
                    connection.ConnectionInfo.HotSyncPort();
                }
                catch (IOException ex) when (ex is not UsbOpenException) { throw new UsbOpenException("接続情報の解析", ex); }
                diagnostics?.Invoke("接続情報の解析: 成功 (Win32=該当なし)");
                connectionInfoReceived?.Invoke(connection.ConnectionInfo);
            }
            cancellation.ThrowIfCancellationRequested();
            var port = connection.ConnectionInfo.HotSyncPort();
            connection.InputPath = device.DevicePath.TrimEnd('\\') + $"\\IN_{port.InEndpoint:D2}";
            connection.OutputPath = device.DevicePath.TrimEnd('\\') + $"\\OUT_{port.OutEndpoint:D2}";
            connection.input = OpenStage(connection.InputPath, $"CreateFile IN_{port.InEndpoint:D2}", diagnostics);
            connection.output = OpenStage(connection.OutputPath, $"CreateFile OUT_{port.OutEndpoint:D2}", diagnostics);
            return connection;
        }
        catch (UsbOpenException ex) { diagnostics?.Invoke(ex.Message); connection.Dispose(); throw; }
        catch { connection.Dispose(); throw; }
    }

    internal static SafeFileHandle OpenStage(string path, string stage, Action<string>? log)
    {
        try
        {
            var handle = Native.Open(path, null);
            log?.Invoke($"{stage}: Win32=0 ({path})");
            return handle;
        }
        catch (IOException ex) { throw new UsbOpenException(stage, ex); }
    }

    internal static byte[] RequestInfo(SafeFileHandle control, uint code, Action<string>? log, CancellationToken cancellation)
    {
        string stage = code == 0x222424 ? "IOCTL 0x222424(旧形式)" : "IOCTL 0x222000(拡張)";
        // Driver requires at least 18 bytes for legacy (eight entries); Request uses returned length only.
        int size = code == 0x222424 ? 18 : 64;
        try
        {
            var bytes = Native.Request(control, code, null, size, 5000, false, null, cancellation);
            log?.Invoke($"{stage}: Win32=0 bytes={bytes.Length}");
            return bytes;
        }
        catch (IOException ex)
        {
            cancellation.ThrowIfCancellationRequested();
            var error = new UsbOpenException(stage, ex);
            log?.Invoke(error.Message);
            throw error;
        }
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timeoutMs);
        lock (readLock)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (buffer.IsEmpty) return 0;
            try
            {
                var storage = Interlocked.Exchange(ref readStorage, null) ?? new Native.RequestStorage(readBuffer.Length);
                int count;
                try
                {
                    count = Native.RequestCount(input!, null, null, Math.Min(buffer.Length, readBuffer.Length),
                        timeoutMs, false, log, readBuffer, storage);
                }
                finally
                {
                    if (!storage.Deferred)
                    {
                        readStorage = storage;
                        if (Volatile.Read(ref disposed) != 0) Interlocked.Exchange(ref readStorage, null)?.Dispose();
                    }
                }
                // A successful zero-byte bulk read can be a USB ZLP, so it is treated
                // like no data. Removal errors are propagated, never treated as timeout.
                readBuffer.AsSpan(0, count).CopyTo(buffer); return count;
            }
            catch (IOException ex) when (UsbByteChannel.CanRetryRead(ex)) { throw; }
            catch { Dispose(); throw; }
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (writeLock)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            try
            {
                while (!data.IsEmpty)
                {
                    int count = Native.Request(output!, null, data.ToArray(), data.Length, 5000, true, log).Length;
                    if (count == 0) throw new IOException("WriteFile: zero-byte write");
                    data = data[count..];
                }
            }
            catch { Dispose(); throw; }
        }
    }

    public void SetBaudRate(int baudRate) { ObjectDisposedException.ThrowIf(disposed != 0, this); }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Interlocked.Exchange(ref readStorage, null)?.Dispose();
        foreach (var handle in new[] { input, output })
        {
            if (handle is null) continue;
            if (!handle.IsClosed && !handle.IsInvalid) Native.CancelIoEx(handle, IntPtr.Zero);
            handle.Dispose();
        }
    }
}
