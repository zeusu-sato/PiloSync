using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PiloSync.Usb;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct InterfaceData
    { public int Size; public Guid Guid; public uint Flags; public nuint Reserved; }
    [StructLayout(LayoutKind.Sequential)] internal struct DevInfo
    { public int Size; public Guid ClassGuid; public uint DevInst; public nuint Reserved; }
    [StructLayout(LayoutKind.Sequential)] internal struct Overlapped
    { public nuint Internal, InternalHigh; public uint Offset, OffsetHigh; public IntPtr Event; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SetupDiGetClassDevsW(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint needed, ref DevInfo info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref DevInfo info, System.Text.StringBuilder id, int size, out int needed);
    [DllImport("setupapi.dll")] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool DeviceIoControl(SafeFileHandle file, uint code, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool ReadFile(SafeFileHandle file, IntPtr buffer, uint count, out uint read, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool WriteFile(SafeFileHandle file, IntPtr buffer, uint count, out uint written, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetOverlappedResult(SafeFileHandle file, IntPtr overlapped, out uint count, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);

    internal static IOException Error(string stage, int error) =>
        new($"{stage}: Win32={error} (0x{error:X8}) {new Win32Exception(error).Message}", new Win32Exception(error));

    internal static SafeFileHandle Open(string path, Action<string>? log)
    {
        var file = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        int error = file.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        log?.Invoke($"CreateFile {path}: Win32={error}");
        if (file.IsInvalid) { file.Dispose(); throw Error("CreateFile " + path, error); }
        return file;
    }

    internal sealed class RequestStorage : IDisposable
    {
        internal readonly EventWaitHandle Event = new(false, EventResetMode.ManualReset);
        internal readonly IntPtr Buffer;
        internal readonly IntPtr Overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<Overlapped>());
        internal bool Deferred;
        internal RequestStorage(int size) => Buffer = Marshal.AllocHGlobal(size + 1);
        public void Dispose()
        { Marshal.FreeHGlobal(Overlapped); Marshal.FreeHGlobal(Buffer); Event.Dispose(); }
    }

    internal static byte[] Request(SafeFileHandle file, uint? ioctl, byte[]? input, int size,
        int timeout, bool write, Action<string>? log, CancellationToken cancellation = default)
    {
        var storage = new RequestStorage(Math.Max(size, input?.Length ?? 0));
        // Deferred storage is released by the completion callback instead.
        var result = new byte[size];
        if (ioctl.HasValue) Marshal.Copy(result, 0, storage.Buffer, result.Length);
        try
        {
            int count = RequestCount(file, ioctl, input, size, timeout, write, log, result, storage, cancellation);
            return count == result.Length ? result : result[..count];
        }
        finally { if (!storage.Deferred) storage.Dispose(); }
    }

    // Each request retains unmanaged storage. A broken driver must never cause a timeout
    // to free an OVERLAPPED or buffer while the kernel still references it.
    internal static int RequestCount(SafeFileHandle file, uint? ioctl, byte[]? input, int size,
        int timeout, bool write, Action<string>? log, byte[] result, RequestStorage storage, CancellationToken cancellation = default)
    {
        var evt = storage.Event;
        IntPtr buffer = storage.Buffer;
        IntPtr ov = storage.Overlapped;
        bool retained = false, deferred = false;
        string stage = ioctl.HasValue ? $"IOCTL 0x{ioctl:X6}" : write ? "WriteFile" : "ReadFile";
        void Cleanup()
        {
            if (deferred) storage.Dispose();
            if (retained) file.DangerousRelease();
        }
        try
        {
            cancellation.ThrowIfCancellationRequested();
            file.DangerousAddRef(ref retained);
            evt.Reset();
            if (input is not null) Marshal.Copy(input, 0, buffer, input.Length);
            Marshal.StructureToPtr(new Overlapped { Event = evt.SafeWaitHandle.DangerousGetHandle() }, ov, false);
            uint count;
            bool ok = ioctl.HasValue
                ? DeviceIoControl(file, ioctl.Value, input is null ? IntPtr.Zero : buffer, (uint)(input?.Length ?? 0), buffer, (uint)size, out count, ov)
                : write ? WriteFile(file, buffer, (uint)input!.Length, out count, ov)
                : ReadFile(file, buffer, (uint)size, out count, ov);
            int error = ok ? 0 : Marshal.GetLastWin32Error();
            using var cancel = cancellation.Register(() => CancelIoEx(file, ov));
            if (!ok && error != 997)
            {
                if (!ioctl.HasValue && !write && error is 121 or 1460)
                { log?.Invoke($"{stage}: timeout Win32={error}"); return 0; }
                throw Error(stage, error);
            }
            if (!ok)
            {
                if (WaitHandle.WaitAny([evt, cancellation.WaitHandle], timeout) != 0)
                {
                    bool cancelled = CancelIoEx(file, ov);
                    int cancelError = cancelled ? 0 : Marshal.GetLastWin32Error();
                    log?.Invoke($"{stage}: timeout; CancelIoEx Win32={cancelError}");
                    if (!evt.WaitOne(2000))
                    {
                        // Keep storage alive until completion without blocking the caller.
                        cancel.Dispose();
                        deferred = true; storage.Deferred = true;
                        ThreadPool.RegisterWaitForSingleObject(evt, (_, _) => Cleanup(), null, Timeout.Infinite, true);
                        throw Error(stage + " cancellation did not complete within 2000 ms", 1460);
                    }
                    if (!GetOverlappedResult(file, ov, out count, false))
                    {
                        error = Marshal.GetLastWin32Error();
                        if (error != 995) throw Error(stage, error);
                    }
                    if (!ioctl.HasValue && !write) count = Math.Max(count, checked((uint)Marshal.PtrToStructure<Overlapped>(ov).InternalHigh));
                    if (!ioctl.HasValue && !write && count > 0)
                    {
                        if (count > size) throw new IOException(stage + ": invalid returned size");
                        Marshal.Copy(buffer, result, 0, checked((int)count)); return (int)count;
                    }
                    if (ioctl.HasValue || write) throw Error(stage + " timeout", 1460);
                    return 0;
                }
                if (!GetOverlappedResult(file, ov, out count, false))
                {
                    error = Marshal.GetLastWin32Error();
                    if (!ioctl.HasValue && !write && error == 995)
                    {
                        count = Math.Max(count, checked((uint)Marshal.PtrToStructure<Overlapped>(ov).InternalHigh));
                        if (count > size) throw new IOException(stage + ": invalid returned size");
                        Marshal.Copy(buffer, result, 0, (int)count); return (int)count;
                    }
                    if (!ioctl.HasValue && !write && error is 121 or 1460)
                    { log?.Invoke($"{stage}: timeout Win32={error}"); return 0; }
                    throw Error(stage, error);
                }
            }
            if (count > size) throw new IOException(stage + ": invalid returned size");
            log?.Invoke($"{stage}: Win32=0 bytes={count}");
            Marshal.Copy(buffer, result, 0, (int)count); return (int)count;
        }
        catch (IOException ex) { log?.Invoke(ex.Message); throw; }
        finally { if (!deferred) Cleanup(); }
    }
}
