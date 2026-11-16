using System.Runtime.ExceptionServices;
using System.ComponentModel;
using PiloSync.Transport;

namespace PiloSync.Usb;

// Owns the lower channel. Read packet-aligned chunks and retain excess bytes.
public sealed class UsbByteChannel : IByteChannel
{
    private readonly IByteChannel connection;
    private readonly object gate = new();
    private readonly byte[] buffer = new byte[65536];
    private int offset, available, requested;
    private bool reading;
    private readonly Thread reader;
    private ExceptionDispatchInfo? failure;
    private bool disposed;
    private int readLimit;
    private readonly Action<string>? log;

    public static int NormalizeReadLimit(int bytes)
    {
        if (bytes < 1 || bytes > 65536) throw new ArgumentOutOfRangeException(nameof(bytes), "USB read size must be 1..65536 bytes");
        return (bytes + 63) / 64 * 64;
    }

    internal static bool CanRetryRead(IOException error) =>
        error.InnerException is Win32Exception win && win.NativeErrorCode is 31 or 87;

    public UsbByteChannel(IByteChannel connection, int readLimit = 64, Action<string>? log = null)
    {
        this.readLimit = NormalizeReadLimit(readLimit);
        this.log = log;
        this.connection = connection;
        reader = new Thread(Pump) { IsBackground = true, Name = "Palm USB read" };
        reader.Start();
    }
    private void Pump()
    {
        try
        {
            while (true)
            {
                int length;
                lock (gate)
                {
                    while (!disposed && requested == 0) Monitor.Wait(gate);
                    if (disposed) return;
                    length = requested; requested = 0; reading = true;
                }
                int n;
                try { n = connection.Read(buffer.AsSpan(0, length), int.MaxValue); }
                catch (IOException ex) when (readLimit > 64 && CanRetryRead(ex))
                {
                    lock (gate) readLimit = 64;
                    log?.Invoke($"USB 受信失敗 ({ex.Message})。この接続の受信上限を64バイトに戻して再試行します");
                    length = 64;
                    n = connection.Read(buffer.AsSpan(0, length), 5000);
                    if (n == 0) throw new IOException("USB read fallback timed out", ex);
                }
                if (n < 0 || n > length) throw new IOException("Invalid USB read count");
                lock (gate)
                {
                    offset = 0; available = n; reading = false;
                    Monitor.PulseAll(gate);
                }
            }
        }
        catch (Exception ex)
        {
            lock (gate) { failure = ExceptionDispatchInfo.Capture(ex); Monitor.PulseAll(gate); }
        }
    }
    public string Name => connection.Name;
    public bool IsSerial => false;
    public int Read(Span<byte> destination, int timeoutMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timeoutMs);
        long deadline = Environment.TickCount64 + timeoutMs;
        lock (gate)
        {
            while (true)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (destination.IsEmpty) return 0;
                if (available > 0) break;
                failure?.Throw();
                int left = (int)Math.Clamp(deadline - Environment.TickCount64, 0, int.MaxValue);
                if (left == 0) return 0;
                if (!reading && requested == 0)
                {
                    requested = (int)Math.Min(((long)destination.Length + 63) / 64 * 64, readLimit);
                    Monitor.PulseAll(gate);
                }
                Monitor.Wait(gate, left);
            }
            int n = Math.Min(available, destination.Length);
            buffer.AsSpan(offset, n).CopyTo(destination);
            offset += n; available -= n;
            return n;
        }
    }
    public void Write(ReadOnlySpan<byte> data) => connection.Write(data);
    public void SetBaudRate(int baud) { }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; Monitor.PulseAll(gate); }
        connection.Dispose();
        if (Thread.CurrentThread != reader) reader.Join(2500);
    }
}
