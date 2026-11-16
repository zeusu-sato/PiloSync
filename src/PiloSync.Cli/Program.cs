using System.Text;
using PiloSync.Protocol;
using PiloSync.Serial;
using PiloSync.Usb;
using System.Diagnostics;

return Entry(args);

static int Entry(string[] args)
{
    if (args.Length == 1 && args[0] == "--version") { Console.WriteLine($"PiloSync {PiloSync.Cli.TrayHost.Version}"); return 0; }
    if (PiloSync.Cli.CommandLine.Classify(args) == PiloSync.Cli.CommandKind.Tray)
    {
        try
        {
            if (args.Length == 0) args = ["tray"];
            var (baud, usbRead) = PiloSync.Cli.CommandLine.ParseTray(args);
            if (PiloSync.Cli.WindowlessProcess.GetConsoleWindow() != IntPtr.Zero) { PiloSync.Cli.WindowlessProcess.Start(Environment.ProcessPath ?? throw new InvalidOperationException("exe の場所が不明です"), args); return 0; }
            return PiloSync.Cli.TrayHost.Run((log, token) => ListenAll(log, token, baud, usbRead), PiloSync.Cli.CommandLine.ParseTrayInstall(args));
        }
        catch (Exception ex) { Console.Error.WriteLine($"PiloSync: {ex.Message}"); return 1; }
    }
    Console.OutputEncoding = Encoding.UTF8;
    var kind = PiloSync.Cli.CommandLine.Classify(args);
    if (kind is PiloSync.Cli.CommandKind.Help or PiloSync.Cli.CommandKind.Unknown) { PiloSync.Cli.CommandLine.Usage(Console.Out); return kind == PiloSync.Cli.CommandKind.Help ? 0 : 1; }
    return Run(args);
}

static async Task ListenAll(Action<string> log, CancellationToken token, int baud, int usbRead)
{
    PiloSync.Cli.InitialSetup.Run(new PiloSync.Cli.PalmRegistry(), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), log);
    void Session(PalmConnection connection, string name)
    {
        PiloSync.Cli.TrayHost.TryClaimRestore(out var restore);
        PiloSync.Cli.TrayHost.Notify("HotSync 開始", name);
        log($"[{name}] 接続 ({connection.Protocol}{(connection.BaudRate > 0 ? $", {connection.BaudRate}bps" : "")})");
        string user = "不明";
        var session = PiloSync.Cli.TrayHost.BeginSession(name);
        try
        {
            PiloSync.Cli.HotSyncSession.Run(connection.Dlp, new PiloSync.Cli.PalmRegistry(), log, connection.TryAbort,
                userKnown: name => { user = name.Length == 0 ? "名前のない端末" : name; session.Rename(user); },
                completed: (ok, reason) => PiloSync.Cli.TrayHost.Notify("HotSync 終了", reason == PiloSync.Cli.HotSyncSession.UnregisteredDeviceMessage
                    ? "名前のない端末です。右クリックの『端末を復元…』から戻せます"
                    : $"{user}：{(restore is null ? "" : "復元 ")}{(ok ? reason.Length == 0 ? "成功" : "成功（一部書けず） " + reason : "失敗 " + reason)}"), progress: session.Report, restore: restore, connection: name);
        }
        finally { session.Dispose(); }
    }
    await Task.WhenAll(PiloSync.Cli.ListenerSupervisor.Run(t => Task.Run(() => new SerialListener(log, Session, baud).Run(null, t)), log, token), PiloSync.Cli.ListenerSupervisor.Run(t => PiloSync.Cli.UsbListener.Run(Session, log, baud, t, null, () => { }, usbRead), log, token));
}

static int Restore(string[] args)
{
    var options = PiloSync.Cli.CommandLine.ParseRestore(args);
    var registry = new PiloSync.Cli.PalmRegistry();
    if (string.IsNullOrWhiteSpace(registry.DataPath) || !File.Exists(Path.Combine(registry.DataPath, "users.dat"))) throw new ArgumentException("Core\\Path または users.dat がありません");
    var store = new PiloSync.Core.AccountStore(registry.DataPath);
    if (!store.Data.Users.Any(a => a.Name == options.AccountName)) throw new ArgumentException("失敗：指定したアカウントがありません");
    using var file = options.LogPath is null ? null : new StreamWriter(options.LogPath, true, new UTF8Encoding(false));
    object gate = new();
    void Log(string line) { lock (gate) { Console.WriteLine(line); file?.WriteLine(line); file?.Flush(); } }
    using var stop = new CancellationTokenSource(); int claimed = 0, exitCode = 1;
    ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); }; Console.CancelKeyPress += cancel;
    try
    {
        void Session(PalmConnection connection, string name)
        {
            if (Interlocked.CompareExchange(ref claimed, 1, 0) != 0)
            {
                Log($"[{name}] 復元は別の端末で実行中のため、この端末は何もしません");
                try { connection.Dlp.EndOfSync(); } catch { connection.TryAbort(); } return;
            }
            try
            {
                Log($"[{name}] 接続 ({connection.Protocol})");
                PiloSync.Cli.HotSyncSession.Run(connection.Dlp, registry, Log, connection.TryAbort,
                    completed: (ok, _) => exitCode = ok ? 0 : 1, restore: new(options.AccountName, options.DryRun), connection: name);
            }
            finally { stop.Cancel(); }
        }
        Log($"次に HotSync した端末に {options.AccountName} のバックアップを書き戻します。端末の中身は上書きされます" + (options.DryRun ? "（--dry-run：確認だけ）" : ""));
        var waits = new List<Task>();
        if (!options.UsbOnly) waits.Add(Task.Run(() => new SerialListener(Log, Session, options.Baud, options.Trace ? Log : null).Run(options.Ports, stop.Token)));
        if (!options.SerialOnly) waits.Add(PiloSync.Cli.UsbListener.Run(Session, Log, options.Baud, stop.Token, options.Trace ? Log : null, () => { }, options.UsbRead));
        try { Task.WhenAll(waits).GetAwaiter().GetResult(); } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        return exitCode;
    }
    finally { stop.Cancel(); Console.CancelKeyPress -= cancel; }
}

static int Run(string[] args)
{
    try
    {
        if (args.FirstOrDefault() == "tidy-backup") return PiloSync.Cli.TidyBackupCommand.Run(args, new PiloSync.Cli.PalmRegistry(), Console.Out);
        if (args.FirstOrDefault() == "schedule") return PiloSync.Cli.ScheduleCommand.Run(args, PiloSync.Core.HotSyncSchedule.DefaultPath, Console.Out);
        if (!PiloSync.Cli.TrayHost.IsRunning())
        {
            var handover = PiloSync.Cli.RenameHandover.Run(new PiloSync.Cli.HandoverEnvironment());
            foreach (string line in handover.Actions.Concat(handover.Warnings)) Console.WriteLine(line);
            if (handover.OldRunning && args.FirstOrDefault() is "listen" or "restore") return 1;
        }
        if (args.Length > 0 && args[0] is "install-startup" or "uninstall-startup") return PiloSync.Cli.StartupCommand.Run(args[0] == "install-startup", args[1..], new PiloSync.Cli.StartupEnvironment(), Console.Out);
        if ((args.FirstOrDefault() is "listen" or "restore") && PiloSync.Cli.TrayHost.IsRunning()) { Console.WriteLine("常駐の PiloSync が動いています。トレイから一時停止するか終了してください"); return 1; }
        if (args.FirstOrDefault() == "restore") return Restore(args);
        if (args.Length > 0 && args[0] == "serial-diag") return SerialDiagnostics.Run(args[1..]);
        if (args.Length > 0 && args[0] == "migrate") return PiloSync.Cli.MigrateCommand.Run(args[1..], new PiloSync.Cli.PalmRegistry(), Console.Out);
        foreach (var process in Process.GetProcessesByName("HotSync"))
        { using (process) Console.WriteLine("純正 HotSync Manager が起動中です。ポートを取り合うので終了してください"); }
        if (args.Length == 1 && args[0] == "list") return PiloSync.Cli.ListCommand.Run();
        if (args.Length == 0 || args[0] != "listen") { PiloSync.Cli.CommandLine.Usage(Console.Out); return 1; }
        string[]? ports = null;
        int baud = 115200, usbRead = 4096; bool once = false, serialOnly = false, usbOnly = false, trace = false; string? logPath = null, assign = null;
        for (int i = 1; i < args.Length; i++)
        {
            string Value() { if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} の値がありません"); return args[++i]; }
            switch (args[i])
            {
                case "--serial-only": serialOnly = true; break;
                case "--usb-only": usbOnly = true; break;
                case "--ports":
                    ports = Value().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (ports.Length == 0 || ports.Any(p => !System.Text.RegularExpressions.Regex.IsMatch(p, @"^COM[1-9][0-9]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))) throw new ArgumentException("COM ポート名を指定してください"); break;
                case "--usb-read": usbRead = UsbByteChannel.NormalizeReadLimit(int.Parse(Value())); break;
                case "--baud": baud = int.Parse(Value()); break;
                case "--trace": trace = true; break;
                case "--once": once = true; break;
                case "--assign": assign = Value(); break;
                case "--log": logPath = Value(); break;
                default: throw new ArgumentException($"不明な引数: {args[i]}");
            }
        }
        if (serialOnly && usbOnly) throw new ArgumentException("--serial-only と --usb-only は同時に指定できません");
        if (!new[] { 9600, 19200, 38400, 57600, 115200 }.Contains(baud)) throw new ArgumentException("baud は 9600,19200,38400,57600,115200 のいずれかです");
        using var file = logPath == null ? null : new StreamWriter(logPath, true, new UTF8Encoding(false));
        object gate = new();
        void Log(string line) { lock (gate) { Console.WriteLine(line); file?.WriteLine(line); file?.Flush(); } }
        void Trace(string line) { lock (gate) { Console.WriteLine(line); file?.WriteLine(line); } }
        void Flush() { lock (gate) file?.Flush(); }
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); }; Console.CancelKeyPress += cancel;
        try
        {
            void Session(PalmConnection connection, string name)
            {
                Log($"[{name}] 接続 ({connection.Protocol}{(connection.BaudRate > 0 ? $", {connection.BaudRate}bps" : "")})");
                try
                {
                    if (PiloSync.Cli.HotSyncSession.Run(connection.Dlp, new PiloSync.Cli.PalmRegistry(), Log, connection.TryAbort, assign, connection: name) && once) stop.Cancel();
                }
                catch { connection.TryAbort(); throw; }
                finally { Flush(); }
            }
            var listener = new SerialListener(Log, Session, baud, trace ? Trace : null);
            PiloSync.Cli.InitialSetup.Run(new PiloSync.Cli.PalmRegistry(), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Log);
            Log($"待ち受け開始: USB={(!serialOnly ? "有効" : "無効")} / シリアル={(usbOnly ? "無効" : ports == null ? "全ポート" : string.Join(",", ports))} / 最大 {baud}bps / Ctrl+C で終了");
            var waits = new List<Task>();
            if (!usbOnly) waits.Add(Task.Run(() => listener.Run(ports, stop.Token)));
            if (!serialOnly) waits.Add(PiloSync.Cli.UsbListener.Run(Session, Log, baud, stop.Token, trace ? Trace : null, Flush, usbRead));
            try { Task.WhenAll(waits).GetAwaiter().GetResult(); }
            finally { stop.Cancel(); }
            return 0;
        }
        finally { Console.CancelKeyPress -= cancel; Flush(); }
    }
    catch (Exception ex) { Console.Error.WriteLine($"PiloSync: {ex.Message}"); return 1; }
}
