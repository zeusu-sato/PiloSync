namespace PiloSync.Transport;

/// <summary>A byte stream. A zero read means timeout; disconnection must throw IOException.
/// Write must send all bytes or throw. Dispose must unblock a pending Read.</summary>
public interface IByteChannel : IDisposable
{
    string Name { get; }
    int Read(Span<byte> buffer, int timeoutMs);
    void Write(ReadOnlySpan<byte> data);
    void SetBaudRate(int baud);
    bool IsSerial { get; }
}
