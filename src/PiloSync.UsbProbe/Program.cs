using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Win32;
using PiloSync.Usb;

Console.OutputEncoding = Encoding.UTF8;
if (args.Length == 0 || args[0] is not ("list" or "watch" or "info") ||
    (args[0] is "list" or "info" && args.Length != 1) ||
    (args[0] == "watch" && (args.Length > 2 || args.Length == 2 && args[1] != "--raw")))
{
    Console.WriteLine("PiloSync.UsbProbe list | info | watch [--raw]"); return 2;
}
if (args[0] == "info")
{
    using var infoStop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; infoStop.Cancel(); };
    try
    {
        Console.WriteLine("HotSync ボタンを押してください（最大 60 秒待ちます）");
        var elapsed = Stopwatch.StartNew();
        IReadOnlyList<UsbDeviceInfo> devices = [];
        while (elapsed.Elapsed < TimeSpan.FromSeconds(60))
        {
            infoStop.Token.ThrowIfCancellationRequested();
            devices = AceecaUsb.Enumerate();
            if (devices.Count > 0) break;
            await Task.Delay(250, infoStop.Token);
        }
        if (devices.Count == 0)
        {
            Console.WriteLine("端末が現れませんでした");
            return 1;
        }
        Console.WriteLine($"現在のUSB端末: {devices.Count} 台 (SetupAPI Win32=0)");
        foreach (var device in devices)
        {
            Console.WriteLine($"{device.ModelName} {device.DevicePath}");
            try
            {
                using var control = AceecaUsbConnection.OpenStage(device.DevicePath, "CreateFile(制御)", Console.WriteLine);
                // Both information IOCTLs are attempted independently, with no data writes or settings IOCTLs.
                foreach (uint code in new uint[] { 0x222000, 0x222424 })
                {
                    try
                    {
                        var bytes = AceecaUsbConnection.RequestInfo(control, code, Console.WriteLine, default);
                        Console.WriteLine($"接続情報 raw IOCTL=0x{code:X6}: {Convert.ToHexString(bytes)}");
                        UsbPortInfo port;
                        try
                        {
                            var info = new UsbConnectionInfo(code, bytes);
                            Console.WriteLine($"接続情報の解析: ポート数={info.Ports.Count} endpointsGiven={info.EndpointsGiven}");
                            foreach (var p in info.Ports)
                                Console.WriteLine($"  function='{p.FunctionId}' port={p.Port} IN={p.InEndpoint} OUT={p.OutEndpoint} HotSync={p.IsHotSync}");
                            port = info.HotSyncPort();
                        }
                        catch (IOException ex) { throw new UsbOpenException("接続情報の解析", ex); }
                        foreach (var (direction, endpoint) in new[] { ("IN", port.InEndpoint), ("OUT", port.OutEndpoint) })
                        {
                            string suffix = $"{direction}_{endpoint:D2}";
                            try
                            {
                                using var pipe = AceecaUsbConnection.OpenStage(device.DevicePath.TrimEnd('\\') + "\\" + suffix,
                                    "CreateFile " + suffix, Console.WriteLine);
                            }
                            catch (UsbOpenException ex) { Console.WriteLine(ex.Message); }
                        }
                    }
                    catch (UsbOpenException ex) { Console.WriteLine(ex.Message); }
                }
            }
            catch (UsbOpenException ex) { Console.WriteLine(ex.Message); }
        }
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
}
if (args[0] == "list")
{
    try
    {
        var devices = AceecaUsb.Enumerate();
        Console.WriteLine($"現在のUSB端末: {devices.Count} 台 (SetupAPI Win32=0)");
        if (devices.Count == 0) Console.WriteLine("端末なし。HotSyncボタンを押した間だけ現れます。");
        foreach (var d in devices) Console.WriteLine($"{d.ModelName} VID={d.Vid:X4} PID={d.Pid:X4}\n  {d.InstanceId}\n  {d.DevicePath}");
        Console.WriteLine("過去の接続履歴 (レジストリを読み取りのみ):");
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB", false);
        int count = 0;
        foreach (string name in root?.GetSubKeyNames() ?? [])
        {
            if (!name.StartsWith("VID_0830", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("VID_082D", StringComparison.OrdinalIgnoreCase)) continue;
            using var device = root!.OpenSubKey(name, false);
            foreach (string instance in device?.GetSubKeyNames() ?? [])
            {
                using var key = device!.OpenSubKey(instance, false);
                Console.WriteLine($"  {name}\\{instance}\n    {key?.GetValue("FriendlyName") ?? key?.GetValue("DeviceDesc")} / Service={key?.GetValue("Service")}");
                count++;
            }
        }
        Console.WriteLine($"履歴: {count} 件"); return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
}

bool raw = args.Contains("--raw");
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
var active = new ConcurrentDictionary<string, Task>(StringComparer.OrdinalIgnoreCase);
var connections = new ConcurrentDictionary<string, AceecaUsbConnection>(StringComparer.OrdinalIgnoreCase);
void Log(string id, string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{id}] {message}");
void ShowInfo(string id, UsbConnectionInfo info)
{
    Log(id, $"接続情報 IOCTL=0x{info.Ioctl:X6} bytes={info.Raw.Length}: {Convert.ToHexString(info.Raw.AsSpan())}");
    Log(id, $"ポート数={info.Ports.Count} endpointsGiven={info.EndpointsGiven}");
    foreach (var p in info.Ports) Log(id, $"function='{p.FunctionId}' port={p.Port} IN={p.InEndpoint} OUT={p.OutEndpoint} HotSync={p.IsHotSync}");
}
void Probe(UsbDeviceInfo device)
{
    string id = device.InstanceId;
    try
    {
        Log(id, $"出現 {device.ModelName} VID={device.Vid:X4} PID={device.Pid:X4}");
        using var connection = AceecaUsbConnection.Open(device, s => Log(id, s), info => ShowInfo(id, info));
        connections[id] = connection;
        if (stop.IsCancellationRequested) return;
        using var channel = new UsbByteChannel(connection);
        var buffer = new byte[64]; int total = 0;
        var elapsed = Stopwatch.StartNew();
        // Accumulate fragmented reads before classifying; stop at 64 bytes or 10 s.
        while (total < buffer.Length && elapsed.ElapsedMilliseconds < 10000 && !stop.IsCancellationRequested)
        {
            int n = channel.Read(buffer.AsSpan(total), (int)Math.Clamp(10000 - elapsed.ElapsedMilliseconds, 0, 1000));
            total += n;
            if (raw && n > 0) Log(id, $"受信断片: {Convert.ToHexString(buffer.AsSpan(total - n, n))}");
            if (n == 0 && total > 0) break;
        }
        if (total == 0) Log(id, "受信なし (10秒または停止)。端末への書き込みは行っていません。");
        else
        {
            Log(id, $"受信 {total} bytes: {Convert.ToHexString(buffer.AsSpan(0, total))}");
            string protocol = total >= 3 && buffer[0] == 0xBE && buffer[1] == 0xEF && buffer[2] == 0xED
                ? "SLP (Handspring系)"
                : total >= 6 && buffer[0] == 1 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(2, 4)) is > 0 and <= 65536
                    ? "NET形式の候補 (Palm OS 4/5系、ヘッダーのみの暫定判定)" : "未判定";
            Log(id, protocol);
        }
    }
    catch (Exception ex) { Log(id, $"失敗: {ex.Message}"); }
    finally { connections.TryRemove(id, out _); Log(id, "クローズ。再出現を待ちます。"); }
}
var watcher = new UsbWatcher();
watcher.EnumerationFailed += ex => Console.Error.WriteLine(ex.Message);
watcher.DeviceArrived += device =>
{
    if (active.TryGetValue(device.InstanceId, out var existing) && !existing.IsCompleted) return;
    active[device.InstanceId] = Task.Run(() => Probe(device));
};
Console.WriteLine("USB待ち受け中 (1秒間隔、送信なし)。HotSyncボタンを押してください。Ctrl+Cで終了。");
await watcher.RunAsync(stop.Token);
foreach (var connection in connections.Values) connection.Dispose();
await Task.WhenAll(active.Values);
return 0;
