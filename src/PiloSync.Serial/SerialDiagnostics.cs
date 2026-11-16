using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using static PiloSync.Serial.SerialNative;
namespace PiloSync.Serial;

public static class SerialDiagnostics
{
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Locate_DevNodeW(out uint node, string id, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_MapCrToWin32Err(uint cr, uint defaultError);
    [SupportedOSPlatform("windows")]
    private static void Device(string port, Action<string> output)
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum");
        foreach (var bus in root?.GetSubKeyNames() ?? [])
        {
            using var b = root!.OpenSubKey(bus);
            foreach (var device in b?.GetSubKeyNames() ?? [])
            {
                using var d = b!.OpenSubKey(device);
                foreach (var instance in d?.GetSubKeyNames() ?? [])
                {
                    using var key = d!.OpenSubKey(instance);
                    using var parameters = key?.OpenSubKey("Device Parameters");
                    if (!string.Equals(parameters?.GetValue("PortName") as string, port, StringComparison.OrdinalIgnoreCase)) continue;
                    using var driver = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + key?.GetValue("Driver"));
                    string ids = string.Join(",", key?.GetValue("HardwareID") as string[] ?? []);
                    output($"デバイス情報: 成功 FriendlyName={key?.GetValue("FriendlyName")} HardwareID={ids} DriverVersion={driver?.GetValue("DriverVersion")} INF={driver?.GetValue("InfPath")}");
                    uint cr = CM_Locate_DevNodeW(out uint node, $"{bus}\\{device}\\{instance}", 0);
                    if (cr != 0) { output($"CM_Locate_DevNode: 失敗 CONFIGRET=0x{cr:X} {Error("CM_Locate_DevNode", (int)CM_MapCrToWin32Err(cr, 31)).Message}"); return; }
                    cr = CM_Get_DevNode_Status(out uint status, out uint problem, node, 0);
                    output($"CM_Get_DevNode_Status: {(cr == 0 ? "成功" : "失敗")} CONFIGRET=0x{cr:X} status=0x{status:X8} problem={problem}" + (cr == 0 ? "" : " " + Error("CM_Get_DevNode_Status", (int)CM_MapCrToWin32Err(cr, 31)).Message)); return;
                }
            }
        }
        output("デバイス情報: 該当なし（このアカウントから確認できません）");
    }
    public static int Run(string[] args)
    {
        var ports = new List<string>(); string? path = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--out") { if (++i == args.Length) throw new ArgumentException("--out の値がありません"); path = args[i]; }
            else if (System.Text.RegularExpressions.Regex.IsMatch(args[i], @"^COM[1-9][0-9]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) ports.Add(args[i].ToUpperInvariant());
            else throw new ArgumentException($"不明な引数: {args[i]}");
        }
        using var file = path == null ? null : new StreamWriter(path, false, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        void Output(string line) { Console.WriteLine(line); file?.WriteLine(line); }
        if (ports.Count == 0) ports.AddRange(SerialPort.GetPortNames());
        if (ports.Count == 0) Output("COMポートがありません。serial-diag COM99 で未接続時の診断を確認できます。");
        foreach (var port in ports.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Output($"=== {port} serial-diag {DateTimeOffset.Now:O} ===");
            try { Diagnose(port, Output); }
            catch (Exception ex) { Output($"診断失敗: {ex.GetType().Name}: {ex.Message}"); }
        }
        return 0;
    }
    private static void Diagnose(string port, Action<string> output)
    {
        bool configurationFailed = false, readOk = false, opened = false;
        int openError = 0;
        bool Step(string stage, Func<string> action)
        {
            try { output($"{stage}: 成功 {action()}"); return true; }
            catch (Exception ex) { output($"{stage}: 失敗 {ex.GetType().Name}: {ex.Message}"); return false; }
        }
        void Check(bool ok, string stage) { if (!ok) throw Error(stage); }
        try { if (OperatingSystem.IsWindows()) Device(port, output); }
        catch (Exception ex) { output($"デバイス状態: 失敗 {ex.GetType().Name}: {ex.Message}"); }
        Microsoft.Win32.SafeHandles.SafeFileHandle? h = null;
        try
        {
            opened = Step("CreateFile", () =>
            {
                try { h = Open(port); return "overlapped"; }
                catch (IOException ex) { if (ex.InnerException is System.ComponentModel.Win32Exception win) openError = win.NativeErrorCode; throw; }
            });
            if (opened)
            {
                var handle = h!;
                Step("GetCommProperties", () =>
                {
                    IntPtr p = Marshal.AllocHGlobal(64);
                    try { Marshal.Copy(new byte[64], 0, p, 64); Check(GetCommProperties(handle, p), "GetCommProperties"); return $"MaxBaud=0x{Marshal.ReadInt32(p, 16):X}"; }
                    finally { Marshal.FreeHGlobal(p); }
                });
                var original = Dcb.Empty; bool gotDcb = false;
                Step("GetCommState", () => { Check(GetCommState(handle, ref original), "GetCommState"); gotDcb = true; return original.ToString(); });
                foreach (int baud in new[] { 9600, 57600 })
                    configurationFailed |= !Step($"SetCommState(normalized,{baud})", () => { var d = Dcb.Empty; d.Minimal(baud); Check(SetCommState(handle, ref d), "SetCommState"); return d.ToString(); });
                foreach (int baud in new[] { 9600, 57600 })
                {
                    if (!gotDcb) { output($"SetCommState(minimal,{baud}): 未実行（GetCommState失敗）"); configurationFailed = true; continue; }
                    configurationFailed |= !Step($"SetCommState(minimal,{baud})", () => { var d = original; d.BaudRate = (uint)baud; d.ByteSize = 8; d.Parity = d.StopBits = 0; d.Flags |= 1; Check(SetCommState(handle, ref d), "SetCommState"); return d.ToString(); });
                }
                configurationFailed |= !Step("BuildCommDCB / SetCommState(9600)", () => { var d = Dcb.BuildBase; Check(BuildCommDCB("baud=9600 parity=N data=8 stop=1", ref d), "BuildCommDCB"); Check(SetCommState(handle, ref d), "SetCommState(build)"); return d.ToString(); });
                Step("SetCommTimeouts", () => { var t = new Timeouts { Interval = uint.MaxValue, Constant = 20, WriteConstant = 5000 }; Check(SetCommTimeouts(handle, ref t), "SetCommTimeouts"); return ""; });
                Step("SetupComm", () => { Check(SetupComm(handle, 4096, 4096), "SetupComm"); return ""; });
                Step("SetCommMask", () => { Check(SetCommMask(handle, 1), "SetCommMask"); return "EV_RXCHAR"; });
                foreach (var (name, function) in new[] { ("SETDTR", 5u), ("SETRTS", 3u), ("CLRDTR", 6u), ("CLRRTS", 4u) })
                    Step($"EscapeCommFunction({name})", () => { Check(EscapeCommFunction(handle, function), name); return ""; });
                Step("GetCommModemStatus", () => { Check(GetCommModemStatus(handle, out uint s), "GetCommModemStatus"); return $"CTS={(s & 16) != 0} DSR={(s & 32) != 0} RI={(s & 64) != 0} DCD={(s & 128) != 0}"; });
                Step("ClearCommError", () => { Check(ClearCommError(handle, out uint e, out var s), "ClearCommError"); return $"errors=0x{e:X} input={s.InQueue} output={s.OutQueue}"; });
                Step("PurgeComm", () => { Check(PurgeComm(handle, 15), "PurgeComm"); return "RX/TX abort/clear（送信なし）"; });
                object gate = new();
                readOk = Step("ReadFile(2秒上限)", () => $"受信={Transfer(handle, gate, () => false, new byte[256], false, 2000)} bytes（0はタイムアウト）");
                Step("WaitCommEvent(200ms)", () => $"mask=0x{Transfer(handle, gate, () => false, new byte[4], false, 200, true):X}");
            }
            else
                foreach (string stage in new[] { "GetCommProperties", "GetCommState", "SetCommState(normalized,9600)", "SetCommState(normalized,57600)", "SetCommState(minimal,9600)", "SetCommState(minimal,57600)", "BuildCommDCB / SetCommState", "SetCommTimeouts", "SetupComm", "SetCommMask", "EscapeCommFunction(SETDTR)", "EscapeCommFunction(SETRTS)", "EscapeCommFunction(CLRDTR)", "EscapeCommFunction(CLRRTS)", "GetCommModemStatus", "ClearCommError", "PurgeComm", "ReadFile", "WaitCommEvent" })
                    output($"{stage}: 未実行（CreateFile失敗）");
        }
        finally { h?.Dispose(); }
        Step("SerialPort.Open/Close（比較のみ）", () =>
        {
            using var p = new SerialPort(port, 9600, Parity.None, 8, StopBits.One) { Handshake = Handshake.None, DtrEnable = true, RtsEnable = true };
            try { p.Open(); p.Close(); }
            catch (Exception ex) { throw new IOException($"{ex.GetType().Name} HRESULT=0x{ex.HResult:X8}（Win32由来なら下位16ビット={ex.HResult & 0xffff}） {ex.Message.ReplaceLineEndings(" ")}", ex); }
            return "";
        });
        bool compatible = false;
        for (int i = 0; i < SerialChannel.Profiles.Length; i++)
        {
            int profile = i;
            compatible |= Step($"互換候補 {i + 1}: {SerialChannel.Profiles[i].Name}", () =>
            {
                using var c = new SerialChannel(port, s => output($"  {s}"), profile);
                return $"受信={c.DiagnosticRead(2000)} bytes（0はタイムアウト、接続・速度の証明ではありません）";
            });
        }
        output(!opened ? openError == 2 ? "推定: 指定ポートが見つかりません。この結果からドライバーの良否は判断できません。"
            : openError == 5 ? "推定: ポートを別のプログラムが使用中、またはこのアカウントのアクセス権が不足しています。"
            : "推定: CreateFileから失敗。31ならドライバー／チップ／USB接続の問題が候補です。"
            : configurationFailed && compatible ? "推定: 設定APIに失敗がありますが、I/O可能な候補があります。互換設定で回避できる可能性があります。"
            : !readOk && !compatible ? "推定: 開いた後のI/Oにも失敗。ドライバー／チップ／USB接続の問題が候補です。"
            : "推定: 読み取り要求は実行できました。Palm接続・速度切替・チップの真正性は未確認です。");
    }
}
