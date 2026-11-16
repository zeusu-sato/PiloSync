using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using PiloSync.Transport;
using static PiloSync.Serial.SerialNative;
namespace PiloSync.Serial;

public sealed class SerialChannel : IByteChannel
{
    internal sealed record Profile(string Name, int DcbMode, bool DcbLines = false, bool Timeouts = true, bool EscapeLines = true);
    internal static readonly Profile[] Profiles = [new("minimal", 1), new("no-dcb", 0), new("build-dcb", 2), new("dcb-lines", 1, true, true, false), new("no-timeouts", 1, false, false), new("bare", 0, false, false, false)];
    // PurgeComm, SetupComm, SetCommMask, modem queries and IOCP binding are omitted in every profile.
    private static readonly ConcurrentDictionary<string, string> remembered = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object cacheGate = new();
    private static string CachePath => Path.Combine(PiloSync.Core.AppFolder.Current, "serial-profiles.json");
    private readonly object gate = new(), readGate = new(), writeGate = new();
    private readonly Action<string>? log, failure;
    private readonly Action? listening;
    private bool listeningNotified;
    private SafeFileHandle handle;
    private bool disposed, transferred;
    private int profileIndex, baud = 9600;
    private readonly HashSet<int> tried = [];
    public string Name { get; }
    public bool IsSerial => true;
    public SerialChannel(string name, Action<string>? log = null) : this(name, log, null) { }
    internal SerialChannel(string name, Action<string>? log, int? profile, Action<string>? failure = null, Action? listening = null)
    {
        Name = name; this.log = log; this.failure = failure ?? log; this.listening = listening;
        lock (cacheGate)
        {
            try
            {
                if (File.Exists(CachePath))
                    foreach (var entry in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CachePath)) ?? []) remembered[entry.Key] = entry.Value;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { log?.Invoke($"互換設定の読み込み: {ex.Message}"); }
        }
        profileIndex = profile ?? 0;
        tried.Add(profileIndex);
        handle = Open(name);
        try { Configure(); }
        catch { handle.Dispose(); throw; }
    }
    private void Check(string stage, bool ok) { if (!ok) failure?.Invoke(Error(stage).Message); }
    private void Configure()
    {
        var p = Profiles[profileIndex];
        if (p.DcbMode != 0)
        {
            var dcb = Dcb.Empty;
            bool ok = true;
            if (p.DcbMode == 2) { dcb = Dcb.BuildBase; ok = BuildCommDCB($"baud={baud} parity=N data=8 stop=1", ref dcb); Check("BuildCommDCB", ok); }
            if (ok) { dcb.Minimal(baud); if (p.DcbLines) dcb.Lines(); Check($"SetCommState({baud},{p.Name})", SetCommState(handle, ref dcb)); }
        }
        if (p.EscapeLines) { Check("EscapeCommFunction(SETDTR)", EscapeCommFunction(handle, 5)); Check("EscapeCommFunction(SETRTS)", EscapeCommFunction(handle, 3)); }
        if (p.Timeouts)
        {
            var timeouts = new Timeouts { Interval = uint.MaxValue, Constant = 20, WriteConstant = 5000 };
            Check("SetCommTimeouts", SetCommTimeouts(handle, ref timeouts));
        }
    }
    private void Remember()
    {
        if (remembered.GetValueOrDefault(Name) == Profiles[profileIndex].Name) return;
        remembered[Name] = Profiles[profileIndex].Name;
        lock (cacheGate)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!); File.WriteAllText(CachePath, JsonSerializer.Serialize(remembered)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log?.Invoke($"互換設定の保存: {ex.Message}"); }
        }
        log?.Invoke($"互換設定を記憶: {Profiles[profileIndex].Name}（I/O成功。速度・端末接続は未確認）");
    }
    public int Read(Span<byte> buffer, int timeoutMs)
    {
        if (buffer.IsEmpty) return 0;
        lock (readGate)
        {
            var bytes = new byte[buffer.Length];
            while (true)
            {
                try
                {
                    int count = Transfer(handle, gate, () => disposed, bytes, false, timeoutMs);
                    lock (gate) { if (disposed) throw Error("ReadFile", 995); Remember(); }
                    if (!listeningNotified) { listeningNotified = true; listening?.Invoke(); }
                    if (count > 0) transferred = true;
                    bytes.AsSpan(0, count).CopyTo(buffer); return count;
                }
                catch (IOException ex)
                {
                    lock (gate)
                    {
                        if (disposed || transferred) throw;
                        int next = Enumerable.Range(0, Profiles.Length).FirstOrDefault(i => !tried.Contains(i), -1);
                        if (next < 0) throw;
                        failure?.Invoke(ex.Message);
                        handle.Dispose(); profileIndex = next; tried.Add(next);
                        log?.Invoke($"互換設定を試行: {Profiles[next].Name}");
                        handle = Open(Name); Configure();
                    }
                }
            }
        }
    }
    internal int DiagnosticRead(int timeout) => Transfer(handle, gate, () => disposed, new byte[256], false, timeout);
    public void Write(ReadOnlySpan<byte> data)
    {
        lock (writeGate)
        {
            while (!data.IsEmpty)
            {
                int count = Transfer(handle, gate, () => disposed, data.ToArray(), true, 5000);
                if (count == 0) throw Error("WriteFile timeout", 1460);
                transferred = true; data = data[count..];
            }
        }
    }
    public void SetBaudRate(int value)
    {
        lock (gate)
        {
            if (disposed) throw Error("SetBaudRate", 995);
            baud = value;
            var dcb = Dcb.Empty;
            bool ok = true;
            if (Profiles[profileIndex].DcbMode == 2) { dcb = Dcb.BuildBase; ok = BuildCommDCB($"baud={value} parity=N data=8 stop=1", ref dcb); Check("BuildCommDCB(baud)", ok); }
            if (ok)
            {
                dcb.Minimal(value); if (Profiles[profileIndex].DcbLines) dcb.Lines();
                ok = SetCommState(handle, ref dcb); Check($"SetCommState(baud={value})", ok);
            }
            if (!ok)
            {
                // Reopening during a Palm handshake would discard received bytes. Try a fresh DCB on this handle.
                dcb = Dcb.BuildBase;
                ok = BuildCommDCB($"baud={value} parity=N data=8 stop=1", ref dcb); Check("BuildCommDCB(baud fallback)", ok);
                if (ok) { dcb.Minimal(value); ok = SetCommState(handle, ref dcb); Check($"SetCommState(baud fallback={value})", ok); }
                if (!ok) failure?.Invoke($"SetBaudRate({value}): 速度変更を確認できません。通信を続行します");
            }
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; CancelIoEx(handle, IntPtr.Zero); handle.Dispose();
        }
    }
}
