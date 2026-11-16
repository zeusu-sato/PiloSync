using PiloSync.Protocol;
using PiloSync.Usb;
namespace PiloSync.Cli;

internal static class UsbListener
{
    public static async Task Run(Action<PalmConnection, string> session, Action<string> log, int baud, CancellationToken cancellation, Action<string>? trace, Action flush, int usbRead)
    {
        var workers = new Dictionary<string, (Task Task, CancellationTokenSource Stop)>(StringComparer.OrdinalIgnoreCase);
        var retry = new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var watcher = new UsbWatcher();
        watcher.EnumerationFailed += ex => log($"USB 一覧の取得失敗: {ex.Message}");
        void Start(UsbDeviceInfo device)
        {
            if (cancellation.IsCancellationRequested) return;
            string id = device.DevicePath;
            if (workers.TryGetValue(id, out var old))
            {
                if (!old.Task.IsCompleted) return;
                old.Stop.Dispose(); workers.Remove(id);
            }
            if (retry.TryGetValue(id, out long next) && (next == long.MaxValue || Environment.TickCount64 < next)) return;
            var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var task = Task.Factory.StartNew(() =>
            {
                string name = $"USB {device.ModelName} VID_{device.Vid:X4}&PID_{device.Pid:X4}";
                bool opened = false, nonHotSync = false;
                var diagnostics = trace is null ? null : new List<string>();
                try
                {
                    stop.Token.ThrowIfCancellationRequested();
                    using var lower = AceecaUsbConnection.Open(device, trace is null ? null : line =>
                    {
                        if (diagnostics is not null) diagnostics.Add(line);
                        else trace($"[{name}] TRACE {line}");
                    }, cancellation: stop.Token, logIo: trace is not null);
                    opened = true;
                    if (diagnostics is not null) foreach (var line in diagnostics) trace!($"[{name}] TRACE {line}");
                    diagnostics = null;
                    using var channel = new UsbByteChannel(trace is not null ? new PiloSync.Transport.TracingChannel(lower, line => trace($"[{name}] TRACE {line}")) : lower, usbRead, line => log($"[{name}] {line}"));
                    using var close = stop.Token.Register(channel.Dispose);
                    stop.Token.ThrowIfCancellationRequested();
                    using var connection = PalmConnection.Accept(channel, baud, stop.Token);
                    session(connection, name);
                }
                catch (Exception ex)
                {
                    if (!stop.IsCancellationRequested)
                    {
                        if (ex is UsbOpenException { IsNonHotSync: true })
                        {
                            nonHotSync = true;
                            trace?.Invoke($"[{name}] TRACE HotSync 以外の用途でつながっています");
                        }
                        else
                        {
                            if (diagnostics is not null) foreach (var line in diagnostics) trace!($"[{name}] TRACE {line}");
                            if (!opened && ex is UsbOpenException usb)
                                log($"[{name}] 開けません: {usb.Message}{(usb.Win32Error is 5 or 32 ? "（純正 HotSync が使用中の可能性）" : "")}");
                            else log($"[{name}] 失敗: {ex.Message}");
                        }
                    }
                }
                finally
                {
                    lock (retry)
                        if (!stop.IsCancellationRequested) retry[id] = nonHotSync ? long.MaxValue : Environment.TickCount64 + 30000;
                    flush();
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            workers[id] = (task, stop);
        }
        watcher.DevicesObserved += devices =>
        {
            var present = devices.Select(d => d.DevicePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var id in retry.Keys) if (!present.Contains(id)) retry.TryRemove(id, out _);
            foreach (var (id, worker) in workers.ToArray())
                if (!present.Contains(id))
                {
                    lock (retry)
                    {
                        worker.Stop.Cancel();
                        retry.TryRemove(id, out _);
                    }
                    if (worker.Task.IsCompleted) { worker.Stop.Dispose(); workers.Remove(id); }
                }
            foreach (var device in devices) Start(device);
        };
        try { await watcher.RunAsync(cancellation); }
        finally
        {
            foreach (var worker in workers.Values) worker.Stop.Cancel();
            await Task.WhenAll(workers.Values.Select(w => w.Task));
            foreach (var worker in workers.Values) worker.Stop.Dispose();
        }
    }
}
