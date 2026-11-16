using System.IO.Ports;
using Microsoft.Win32;
using PiloSync.Protocol;
namespace PiloSync.Serial;

public sealed record SerialPortInfo(string Name, string FriendlyName, bool IsCh340);
public static class SerialPorts
{
    // Enumeration only: never opens a COM port or modifies the registry.
    public static IReadOnlyList<SerialPortInfo> List()
    {
        var names = SerialPort.GetPortNames().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows()) ReadFriendlyNames(details);
        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Select(n =>
        {
            string friendly = details.GetValueOrDefault(n, n);
            return new SerialPortInfo(n, friendly, friendly.Contains("CH340", StringComparison.OrdinalIgnoreCase) || friendly.Contains("CH341", StringComparison.OrdinalIgnoreCase));
        }).ToArray();
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void ReadFriendlyNames(Dictionary<string, string> result)
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum");
        if (root == null) return;
        foreach (string bus in root.GetSubKeyNames())
        {
            using var busKey = root.OpenSubKey(bus); if (busKey == null) continue;
            foreach (string device in busKey.GetSubKeyNames())
            {
                using var deviceKey = busKey.OpenSubKey(device); if (deviceKey == null) continue;
                foreach (string instance in deviceKey.GetSubKeyNames())
                {
                    try
                    {
                        using var instanceKey = deviceKey.OpenSubKey(instance);
                        using var parameters = instanceKey?.OpenSubKey("Device Parameters");
                        if (parameters?.GetValue("PortName") is string port)
                            result[port] = instanceKey?.GetValue("FriendlyName") as string ?? instanceKey?.GetValue("DeviceDesc") as string ?? port;
                    }
                    catch (System.Security.SecurityException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
    }
}

/// <summary>One dedicated thread per port; no fixed limit. Run until cancellation.</summary>
public sealed class SerialListener(Action<string> log, Action<PalmConnection, string> session, int baud = 115200, Action<string>? trace = null)
{
    private sealed record Worker(Thread Thread, CancellationTokenSource Stop);
    public void Run(IReadOnlyCollection<string>? selected, CancellationToken cancellation)
    {
        var workers = new Dictionary<string, Worker>(StringComparer.OrdinalIgnoreCase);
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                string[] names;
                try { names = selected?.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? SerialPort.GetPortNames().Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); }
                catch (Exception ex) { log($"ポート一覧の取得失敗: {ex.Message}"); cancellation.WaitHandle.WaitOne(3000); continue; }
                foreach (var name in workers.Keys.Except(names, StringComparer.OrdinalIgnoreCase).ToArray())
                {
                    var w = workers[name]; w.Stop.Cancel(); w.Thread.Join(); w.Stop.Dispose(); workers.Remove(name);
                }
                foreach (string name in names)
                {
                    if (workers.ContainsKey(name)) continue;
                    var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    var thread = new Thread(() => Listen(name, stop.Token)) { IsBackground = true, Name = $"PiloSync {name}" };
                    workers.Add(name, new(thread, stop)); thread.Start();
                }
                cancellation.WaitHandle.WaitOne(3000);
            }
        }
        finally
        {
            foreach (var w in workers.Values) w.Stop.Cancel();
            foreach (var w in workers.Values) { w.Thread.Join(); w.Stop.Dispose(); }
        }
    }
    private void Listen(string name, CancellationToken cancellation)
    {
        var retry = new SerialRetry();
        var settingsErrors = new Dictionary<string, string>();
        var messages = new SerialHandshakeLog(message => log($"[{name}] {message}"),
            trace == null ? null : message => trace($"[{name}] {message}"));
        using var timer = new Timer(_ => messages.CheckExpired(), null, 250, 250);
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                messages.BeginAttempt();
                try
                {
                    using var channel = new SerialChannel(name, messages.Information, null, message =>
                    {
                        string stage = message.Split(':', 2)[0];
                        if (settingsErrors.GetValueOrDefault(stage) != message) messages.Setting(message);
                        settingsErrors[stage] = message;
                    }, messages.Listening);
                    using var close = cancellation.Register(() => { try { channel.Dispose(); } catch (IOException) { } });
                    using var connection = PalmConnection.Accept(channel, baud, cancellation);
                    // Reset only after a handshake: CH340 may open fine and then fail every ReadFile.
                    retry.Opened();
                    if (retry.Recovered()) messages.Failure("復帰");
                    messages.Accepted();
                    if (connection.SkippedNoiseBytes > 0) messages.Information($"雑音 {connection.SkippedNoiseBytes} バイトを破棄");
                    settingsErrors.Clear();
                    session(connection, name);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
                catch (Exception ex) { if (!cancellation.IsCancellationRequested && retry.Changed(ex.Message)) messages.Failure($"失敗: {ex.Message}"); }
                cancellation.WaitHandle.WaitOne(retry.NextDelay());
            }
        }
        finally { messages.Flush(); }
    }
}

internal sealed class SerialRetry
{
    private string? lastError;
    private int attempt;
    internal bool Changed(string error) { bool changed = lastError != error; lastError = error; return changed; }
    internal void Opened() => attempt = 0;
    internal bool Recovered() { bool failed = lastError != null; lastError = null; return failed; }
    internal int NextDelay() { int delay = new[] { 500, 1000, 2000, 3000 }[attempt]; attempt = Math.Min(attempt + 1, 3); return delay; }
}

internal sealed class SerialHandshakeLog(Action<string> log, Action<string>? trace = null,
    Func<TimeSpan>? clock = null, TimeSpan? grace = null)
{
    private readonly object gate = new();
    private readonly List<string> pending = [];
    private readonly Func<TimeSpan> now = clock ?? (() => TimeSpan.FromMilliseconds(Environment.TickCount64));
    private readonly TimeSpan delay = grace ?? TimeSpan.FromSeconds(30);
    private TimeSpan? first;
    private bool accepted, expired;
    internal void BeginAttempt() { lock (gate) accepted = false; }
    internal void Information(string message) { lock (gate) log(message); }
    internal void Failure(string message)
    {
        lock (gate)
        {
            CheckExpired();
            if (accepted || (expired && message == "復帰")) { log(message); return; }
            expired = false;
            first ??= now();
            pending.Add(message);
        }
    }
    internal void Setting(string message)
    {
        lock (gate)
        {
            CheckExpired();
            if (accepted) { log(message); return; }
            pending.Add(message);
        }
    }
    internal void Listening()
    {
        lock (gate)
        {
            CheckExpired();
            foreach (string message in pending) trace?.Invoke(message);
            pending.Clear(); first = null;
        }
    }
    internal void CheckExpired()
    {
        lock (gate)
        {
            if (first == null || now() - first.Value < delay) return;
            Flush(); expired = true;
        }
    }
    internal void Accepted()
    {
        lock (gate)
        {
            CheckExpired();
            foreach (string message in pending) trace?.Invoke(message);
            pending.Clear(); first = null; expired = false; accepted = true;
        }
    }
    internal void Flush()
    {
        lock (gate)
        {
            foreach (string message in pending) log(message);
            pending.Clear(); first = null;
        }
    }
}
