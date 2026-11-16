namespace PiloSync.Cli;
public enum CommandKind { Tray, Help, Command, Unknown }
public sealed record RestoreOptions(string AccountName, bool DryRun, bool SerialOnly, bool UsbOnly, string[]? Ports, int Baud, int UsbRead, string? LogPath, bool Trace);
public sealed record TidyBackupOptions(string AccountName, bool DryRun, string? LogPath = null);
public sealed record ScheduleOptions(string Action, int[] Minutes);
public static class CommandLine
{
    public static CommandKind Classify(string[] args) => args.FirstOrDefault() switch
    {
        null or "tray" => CommandKind.Tray,
        "help" or "--help" or "-h" or "/?" => CommandKind.Help,
        "tidy-backup" or "schedule" or "restore" or "listen" or "list" or "migrate" or "serial-diag" or "install-startup" or "uninstall-startup" => CommandKind.Command,
        _ => CommandKind.Unknown
    };
    public static TidyBackupOptions ParseTidyBackup(string[] args)
    {
        if (args.Length < 2 || args[0] != "tidy-backup" || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith("--")) throw new ArgumentException("アカウント名が必要です");
        bool dry = false; string? log = null;
        for (int i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dry-run": dry = true; break;
                case "--log":
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--")) throw new ArgumentException($"{args[i]} の値がありません");
                    log = args[++i]; break;
                default: throw new ArgumentException($"不明な引数: {args[i]}");
            }
        }
        return new(args[1], dry, log);
    }
    public static ScheduleOptions ParseSchedule(string[] args)
    {
        if (args.Length == 0 || args[0] != "schedule") throw new ArgumentException("schedule が必要です");
        if (args.Length == 1) return new("list", []);
        if (args[1] == "clear" && args.Length == 2) return new("clear", []);
        if (args[1] != "set") throw new ArgumentException($"不明な引数: {args[1]}");
        var times = args[2..];
        if (times.Length == 0) throw new ArgumentException("時刻を指定してください");
        if (times.Length > PiloSync.Core.HotSyncSchedule.MaxTimes) throw new ArgumentException($"時刻は {PiloSync.Core.HotSyncSchedule.MaxTimes} 個までです");
        var minutes = new int[times.Length];
        for (int i = 0; i < times.Length; i++)
            if (!PiloSync.Core.HotSyncSchedule.TryParse(times[i], out minutes[i])) throw new ArgumentException($"時刻を解釈できません: {times[i]}");
        return new("set", minutes);
    }
    public static RestoreOptions ParseRestore(string[] args)
    {
        if (args.Length < 2 || args[0] != "restore" || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith("--")) throw new ArgumentException("アカウント名が必要です");
        bool dry = false, serial = false, usb = false, trace = false; int baud = 115200, read = 4096; string[]? ports = null; string? log = null;
        for (int i = 2; i < args.Length; i++)
        {
            string Value() { if (i + 1 >= args.Length || args[i + 1].StartsWith("--")) throw new ArgumentException($"{args[i]} の値がありません"); return args[++i]; }
            switch (args[i])
            {
                case "--dry-run": dry = true; break;
                case "--serial-only": serial = true; break;
                case "--usb-only": usb = true; break;
                case "--trace": trace = true; break;
                case "--baud": baud = int.Parse(Value()); break;
                case "--usb-read": read = PiloSync.Usb.UsbByteChannel.NormalizeReadLimit(int.Parse(Value())); break;
                case "--log": log = Value(); break;
                case "--ports":
                    ports = Value().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (ports.Length == 0 || ports.Any(p => !System.Text.RegularExpressions.Regex.IsMatch(p, @"^COM[1-9][0-9]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))) throw new ArgumentException("COM ポート名を指定してください"); break;
                default: throw new ArgumentException($"不明な引数: {args[i]}");
            }
        }
        if (serial && usb) throw new ArgumentException("--serial-only と --usb-only は同時に指定できません");
        if (!new[] { 9600, 19200, 38400, 57600, 115200 }.Contains(baud)) throw new ArgumentException("baud は 9600,19200,38400,57600,115200 のいずれかです");
        return new(args[1], dry, serial, usb, ports, baud, read, log, trace);
    }
    public static string[] ParseTrayInstall(string[] args)
    {
        int index = Array.IndexOf(args, "--install");
        if (index < 0) return [];
        string[] paths = args[(index + 1)..];
        if (paths.Length == 0 || paths.Any(p => string.IsNullOrWhiteSpace(p) || p.StartsWith("--"))) throw new ArgumentException("--install の後にファイルを指定してください。");
        return paths.Select(Path.GetFullPath).ToArray();
    }
    public static (int baud, int usbRead) ParseTray(string[] args)
    {
        int baud = 115200, usbRead = 4096;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--install") { ParseTrayInstall(args); break; }
            if (args[i] is not ("--baud" or "--usb-read")) throw new ArgumentException($"不明な引数: {args[i]}");
            string option = args[i]; if (++i >= args.Length) throw new ArgumentException($"{option} の値がありません");
            if (option == "--baud") baud = int.Parse(args[i]); else usbRead = PiloSync.Usb.UsbByteChannel.NormalizeReadLimit(int.Parse(args[i]));
        }
        if (!new[] { 9600, 19200, 38400, 57600, 115200 }.Contains(baud)) throw new ArgumentException("baud は 9600,19200,38400,57600,115200 のいずれかです");
        return (baud, usbRead);
    }
    public static void Usage(TextWriter output) => output.WriteLine("pilosync-cli.exe --version\n  serial-diag [COM3 ...] [--out <path>]\n  migrate [--dry-run] [--to <dir>] | migrate --undo [--dry-run]\n  list\n  listen [--serial-only|--usb-only] [--ports COM3,COM4] [--baud 115200] [--once] [--assign <name>] [--log <path>] [--trace] [--usb-read <bytes>]\n  restore <アカウント名> [--serial-only|--usb-only] [--ports COM3,COM4] [--baud 115200] [--log <path>] [--dry-run] [--usb-read <bytes>] [--trace]\n  tidy-backup <アカウント名> [--dry-run] [--log <path>]\n  schedule | schedule set <時刻...> | schedule clear\n  tray [--baud 115200] [--usb-read <bytes>] [--install <files...>]\n  install-startup [--dry-run] | uninstall-startup [--dry-run]");
}
