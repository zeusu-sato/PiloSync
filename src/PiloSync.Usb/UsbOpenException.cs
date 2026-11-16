using System.ComponentModel;

namespace PiloSync.Usb;

public sealed class UsbOpenException : IOException
{
    public string Stage { get; }
    public bool IsNonHotSync { get; }
    public int? Win32Error { get; }

    internal UsbOpenException(string stage, Exception cause, UsbOpenException? previous = null, bool isNonHotSync = false)
        : base($"{(previous is null ? "" : previous.Message + "; ")}{stage}: " +
            (ErrorCode(cause) is int code ? $"Win32={code}: " : "Win32=該当なし: ") + cause.Message, cause)
    { Stage = stage; Win32Error = ErrorCode(cause); IsNonHotSync = isNonHotSync; }

    private static int? ErrorCode(Exception cause) => cause switch
    {
        UsbOpenException usb => usb.Win32Error,
        Win32Exception win => win.NativeErrorCode,
        _ => cause.InnerException is null ? null : ErrorCode(cause.InnerException)
    };
}
