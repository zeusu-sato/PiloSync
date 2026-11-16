using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PiloSync.Serial;

internal static class SerialNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Dcb
    {
        internal uint Length, BaudRate, Flags;
        internal ushort Reserved, XonLim, XoffLim;
        internal byte ByteSize, Parity, StopBits, XonChar, XoffChar, ErrorChar, EofChar, EvtChar;
        internal ushort Reserved1;
        internal static Dcb Empty => new() { Length = (uint)Marshal.SizeOf<Dcb>() };
        internal static Dcb BuildBase => new() { Length = (uint)Marshal.SizeOf<Dcb>(), Flags = 1, XonChar = 17, XoffChar = 19 };
        internal void Minimal(int baud)
        {
            // Do not inherit driver thresholds or flow-control flags (CH340 may reject large thresholds at 9600).
            this = new Dcb
            {
                Length = (uint)Marshal.SizeOf<Dcb>(), BaudRate = (uint)baud,
                Flags = 0x1011, ByteSize = 8, XonChar = 17, XoffChar = 19,
                XonLim = 0, XoffLim = 0
            };
        }
        internal void Lines() { Flags = (Flags & ~0x3030u) | 0x1010u; }
        public override readonly string ToString() => $"baud={BaudRate} data={ByteSize} parity={Parity} stop={StopBits} flags=0x{Flags:X8} Binary={Flags & 1} CTS={(Flags >> 2) & 1} DSR={(Flags >> 3) & 1} DTR={(Flags >> 4) & 3} RTS={(Flags >> 12) & 3} OutX={(Flags >> 8) & 1} InX={(Flags >> 9) & 1} AbortOnError={(Flags >> 14) & 1} Xon/Xoff={XonChar:X2}/{XoffChar:X2} Error/Eof={ErrorChar:X2}/{EofChar:X2} XonLim/XoffLim={XonLim}/{XoffLim}";
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Timeouts { internal uint Interval, Multiplier, Constant, WriteMultiplier, WriteConstant; }
    [StructLayout(LayoutKind.Sequential)] internal struct Overlap { internal UIntPtr Internal, InternalHigh; internal uint Offset, OffsetHigh; internal IntPtr Event; }
    [StructLayout(LayoutKind.Sequential)] internal struct Stat { internal uint Flags, InQueue, OutQueue; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetCommState(SafeFileHandle h, ref Dcb dcb);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetCommState(SafeFileHandle h, ref Dcb dcb);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool BuildCommDCB(string text, ref Dcb dcb);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetCommTimeouts(SafeFileHandle h, ref Timeouts timeouts);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetCommProperties(SafeFileHandle h, IntPtr properties);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetupComm(SafeFileHandle h, uint input, uint output);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetCommMask(SafeFileHandle h, uint mask);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool EscapeCommFunction(SafeFileHandle h, uint function);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetCommModemStatus(SafeFileHandle h, out uint status);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool ClearCommError(SafeFileHandle h, out uint errors, out Stat stat);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool PurgeComm(SafeFileHandle h, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool ReadFile(SafeFileHandle h, IntPtr buffer, uint size, IntPtr count, IntPtr overlap);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool WriteFile(SafeFileHandle h, IntPtr buffer, uint size, IntPtr count, IntPtr overlap);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool WaitCommEvent(SafeFileHandle h, IntPtr mask, IntPtr overlap);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr overlap, out uint count, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlap);
    internal static IOException Error(string stage, int? code = null)
    {
        int error = code ?? Marshal.GetLastWin32Error();
        return new IOException($"{stage}: Win32={error} {new Win32Exception(error).Message}" + (error == 5 ? "（別のプログラムが使用中、またはアクセス権不足）" : ""), new Win32Exception(error));
    }
    internal static SafeFileHandle Open(string port)
    {
        var h = CreateFile(@"\\.\" + port, 0xC0000000, 0, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (!h.IsInvalid) return h;
        var error = Error("CreateFile"); h.Dispose(); throw error;
    }

    // The handle, event, OVERLAPPED and buffer remain alive until completion, including cancellation.
    internal static int Transfer(SafeFileHandle h, object gate, Func<bool> disposed, byte[] bytes, bool write, int timeout, bool commEvent = false)
    {
        string stage = commEvent ? "WaitCommEvent" : write ? "WriteFile" : "ReadFile";
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset);
        IntPtr buffer = Marshal.AllocHGlobal(Math.Max(4, bytes.Length));
        IntPtr ov = Marshal.AllocHGlobal(Marshal.SizeOf<Overlap>());
        bool held = false, pending = false;
        SafeFileHandle? operation = null;
        try
        {
            // Low bit suppresses IOCP notification; this operation is completed through its event only.
            Marshal.StructureToPtr(new Overlap { Event = new IntPtr(ready.SafeWaitHandle.DangerousGetHandle().ToInt64() | 1) }, ov, false);
            if (write) Marshal.Copy(bytes, 0, buffer, bytes.Length);
            lock (gate)
            {
                if (disposed()) throw Error(stage, 995);
                h.DangerousAddRef(ref held);
                operation = new SafeFileHandle(h.DangerousGetHandle(), false);
                bool ok = commEvent ? WaitCommEvent(operation!, buffer, ov) : write
                    ? WriteFile(operation!, buffer, (uint)bytes.Length, IntPtr.Zero, ov)
                    : ReadFile(operation!, buffer, (uint)bytes.Length, IntPtr.Zero, ov);
                if (!ok && Marshal.GetLastWin32Error() != 997) throw Error(stage);
                pending = true;
            }
            bool timedOut = !ready.WaitOne(Math.Max(0, timeout));
            if (timedOut) CancelIoEx(operation!, ov);
            bool complete = GetOverlappedResult(operation!, ov, out uint count, true);
            int error = complete ? 0 : Marshal.GetLastWin32Error();
            pending = false;
            if (!complete && !(timedOut && error == 995)) throw Error(stage + "/GetOverlappedResult", error);
            if (timedOut && error == 995) return 0;
            if (!write && !commEvent && count > 0) Marshal.Copy(buffer, bytes, 0, checked((int)count));
            return commEvent ? Marshal.ReadInt32(buffer) : checked((int)count);
        }
        finally
        {
            if (pending) { CancelIoEx(operation!, ov); GetOverlappedResult(operation!, ov, out _, true); }
            operation?.Dispose();
            if (held) h.DangerousRelease();
            Marshal.FreeHGlobal(ov); Marshal.FreeHGlobal(buffer);
        }
    }
}
