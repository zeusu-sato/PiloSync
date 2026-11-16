namespace PiloSync.Usb;

public sealed class UsbWatcher
{
    public event Action<UsbDeviceInfo>? DeviceArrived;
    public event Action<Exception>? EnumerationFailed;
    public event Action<IReadOnlyList<UsbDeviceInfo>>? DevicesObserved;

    // Notifications run on the polling task. Subscribers should dispatch long work
    // themselves so other handhelds can be discovered while one is being read.
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var previous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                IReadOnlyList<UsbDeviceInfo> devices;
                try { devices = AceecaUsb.Enumerate(); }
                catch (IOException ex) { EnumerationFailed?.Invoke(ex); continue; }
                DevicesObserved?.Invoke(devices);
                var current = devices.Select(d => d.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var device in devices)
                    if (!previous.Contains(device.InstanceId)) DeviceArrived?.Invoke(device);
                previous = current;
            } while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}
