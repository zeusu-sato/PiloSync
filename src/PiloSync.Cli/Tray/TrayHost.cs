using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using PiloSync.Protocol;
using PiloSync.Cli.Update;
using Microsoft.Win32;

namespace PiloSync.Cli;

public static class ListenerSupervisor
{
    public static async Task Run(Func<CancellationToken, Task> listen, Action<string> log, CancellationToken token, int retryMilliseconds = 2000)
    {
        while (!token.IsCancellationRequested)
        {
            try { await listen(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { log(ex.ToString()); }
            if (!token.IsCancellationRequested)
                try { await Task.Delay(retryMilliseconds, token); } catch (OperationCanceledException) { break; }
        }
    }
}

public static partial class TrayHost
{
    public static string Version => typeof(TrayHost).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).Cast<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "開発版";
    private const string MutexName = @"Local\PiloSync.Tray";
    private static NotifyIcon? icon;
    private static Control? dispatcher;
    private static readonly TrayStatus Status = new();
    private static readonly NextSyncRequest<RestoreRequest> NextSync = new();
    public static bool TryClaimRestore(out RestoreRequest? request) { bool claimed = NextSync.TryClaim(out request); if (claimed) Update(); return claimed; }
    private static Action? refresh;
    private static int pending;
    public static Session BeginSession(string name) { var session = new Session(Status.Begin(name)); Update(); return session; }
    public sealed class Session(TrayStatus.Session session) : IDisposable
    {
        public void Rename(string name) { session.Rename(name); Update(); }
        public void Report(SyncProgress progress) { session.Report(progress); Update(); }
        public void Dispose() { session.Dispose(); Update(); }
    }
    private static void Update()
    {
        if (Interlocked.Exchange(ref pending, 1) != 0) return;
        try
        {
            var control = dispatcher;
            if (control is null) { Interlocked.Exchange(ref pending, 0); return; }
            control.BeginInvoke((Action)(() => { Interlocked.Exchange(ref pending, 0); refresh?.Invoke(); }));
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref pending, 0); }
    }
    private static readonly object LogGate = new();
    public static string LogDirectory => Path.Combine(PiloSync.Core.AppFolder.Current, "Logs");
    private static string TodayLog => Path.Combine(LogDirectory, $"PiloSync-{DateTime.Now:yyyyMMdd}.log");
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
    public static bool IsRunning() { if (!Mutex.TryOpenExisting(MutexName, out var mutex)) return false; mutex.Dispose(); return true; }
    public static void Notify(string title, string message)
    {
        try { dispatcher?.BeginInvoke((Action)(() => icon?.ShowBalloonTip(5000, title, message.Length > 240 ? message[..240] : message, ToolTipIcon.Info))); } catch (InvalidOperationException) { }
    }
    private static void Log(string text) { try { lock (LogGate) { Directory.CreateDirectory(LogDirectory); File.AppendAllText(TodayLog, text + Environment.NewLine, new UTF8Encoding(false)); } } catch (Exception ex) { Crash(ex); } }
    private static void Crash(Exception ex)
    {
        try { lock (LogGate) { Directory.CreateDirectory(LogDirectory); File.AppendAllText(Path.Combine(LogDirectory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log"), ex + Environment.NewLine, new UTF8Encoding(false)); } } catch { }
    }
    private static void AddDeviceActions(ContextMenuStrip menu, Func<bool> paused, Action refreshStatus)
    {
        var restore = menu.Items.Add("端末を復元…");
        menu.Opening += (_, _) => { var request = NextSync.Current; restore.Text = request is null ? "端末を復元…" : $"端末の復元を取り消す（{request.AccountName}）"; };
        restore.Click += (_, _) =>
        {
            if (NextSync.Current is not null) { NextSync.Cancel(); refreshStatus(); Notify("PiloSync", "端末の復元予約を取り消しました"); return; }
            try { SelectRestore(paused, refreshStatus); }
            catch (Exception ex) { Log(ex.ToString()); Notify("端末を復元", ex.Message); }
        };
        menu.Items.Add(new ToolStripSeparator());
    }
    private static void SelectRestore(Func<bool> paused, Action refreshStatus)
    {
        string? path = new PalmRegistry().DataPath;
        if (string.IsNullOrWhiteSpace(path)) { Notify("端末を復元", "Core\\Path が設定されていません。"); return; }
        var store = new PiloSync.Core.AccountStore(path);
        if (store.Data.Users.Count == 0) { Notify("端末を復元", "復元できるアカウントがありません。"); return; }
        using var form = new Form { Text = "端末を復元", ClientSize = new(540, 280), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterScreen };
        using var list = new ListBox { Location = new(12, 12), Size = new(516, 210), HorizontalScrollbar = true };
        var accounts = store.Data.Users.ToArray();
        foreach (var account in accounts)
        {
            string detail;
            try
            {
                string backup = Path.Combine(store.GetUserFolder(account), "Backup");
                var files = Directory.Exists(backup) ? new DirectoryInfo(backup).GetFiles() : [];
                detail = files.Length == 0 ? "0 ファイル" : $"{files.Length} ファイル / 最終更新 {files.Max(f => f.LastWriteTime):yyyy/MM/dd HH:mm}";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { detail = "バックアップ情報を取得できません"; }
            list.Items.Add($"{account.Name}（{detail}）");
        }
        using var ok = new Button { Text = "復元する", Location = new(326, 238), Size = new(96, 30) };
        using var cancel = new Button { Text = "キャンセル", Location = new(432, 238), Size = new(96, 30), DialogResult = DialogResult.Cancel };
        form.Controls.AddRange([list, ok, cancel]); form.AcceptButton = ok; form.CancelButton = cancel; list.SelectedIndex = 0;
        ok.Click += (_, _) =>
        {
            if (list.SelectedIndex < 0) return;
            string name = accounts[list.SelectedIndex].Name;
            if (MessageBox.Show(form, $"次に HotSync した端末に{name}のバックアップを書き戻します。端末の中身は上書きされます。", "端末を復元", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            NextSync.Reserve(new(name)); refreshStatus();
            Notify("PiloSync", paused() ? "一時停止中です。再開すると次の HotSync で復元します" : $"次の HotSync で{name}を復元します");
            form.DialogResult = DialogResult.OK;
        };
        form.ShowDialog();
    }
    public static int Run(Func<Action<string>, CancellationToken, Task> listen, string[]? installFiles = null)
    {
        FreeConsole();
        using var mutex = new Mutex(true, MutexName, out bool created);
        installFiles ??= [];
        if (!created && installFiles.Length > 0)
        {
            try { InstallPipe.Send(installFiles).GetAwaiter().GetResult(); return 0; }
            catch (Exception ex) { Log(ex.ToString()); MessageBox.Show(ex.Message, "インストール一覧を渡せません", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
        }
        if (!created) { try { using var signal = EventWaitHandle.OpenExisting(@"Local\PiloSync.Tray.Activate"); signal.Set(); } catch (WaitHandleCannotBeOpenedException) { } return 1; }
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\PiloSync.Tray.Activate");
        int result = 0;
        using var releaseRequest = new ManualResetEvent(false);
        using var released = new ManualResetEvent(false);
        using var finished = new ManualResetEvent(false);
        var thread = new Thread(() =>
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => Crash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject.ToString()));
            TaskScheduler.UnobservedTaskException += (_, e) => { Crash(e.Exception); e.SetObserved(); };
            using var control = new Control(); dispatcher = control; control.CreateControl();
            using var menu = new ContextMenuStrip();
            int size = SystemInformation.SmallIconSize.Width;
            using var idle = TrayIcons.Create(size, 0, false);
            using var pausedIcon = TrayIcons.Create(size, 0, true);
            var frames = new List<System.Drawing.Icon>();
            using var timer = new System.Windows.Forms.Timer { Interval = 175 };
            using var trayIcon = new NotifyIcon { Icon = idle, Text = Status.Snapshot().Text, ContextMenuStrip = menu, Visible = true }; icon = trayIcon;
            var activation = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) => Notify("PiloSync", "起動中です。通知領域のアイコンから操作できます。"), null, Timeout.Infinite, false);
            bool paused = false; int frame = 0;
            void Refresh()
            {
                var state = Status.Snapshot(paused, NextSync.Current?.AccountName); trayIcon.Text = state.Text;
                if (!paused && state.Count > 0)
                {
                    if (!timer.Enabled) { frame = 0; trayIcon.Icon = frames[frame]; timer.Start(); }
                }
                else { timer.Stop(); frame = 0; trayIcon.Icon = paused ? pausedIcon : idle; }
            }
            timer.Tick += (_, _) => { frame = (frame + 1) % TrayIcons.FrameCount; trayIcon.Icon = frames[frame]; };
            refresh = Refresh;
            CancellationTokenSource? stop = null; Task worker = Task.CompletedTask;
            using var pipeStop = new CancellationTokenSource();
            Task pipeWorker = Task.CompletedTask;
            CancellationTokenSource? updateStop = null;
            Task updateWorker = Task.CompletedTask;
            string? currentTag = UpdateFetcher.CurrentTag;
            using var settingsKey = Registry.CurrentUser.CreateSubKey(@"Software\PiloSync");
            var settings = new UpdateSettings(settingsKey);
            bool updateEnabled = settings.Enabled;
            async Task<bool> TryInstall(StagedUpdate staged, CancellationToken token)
            {
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    control.BeginInvoke((Action)(async () =>
                    {
                        try
                        {
                            if (token.IsCancellationRequested || !UpdateGate.CanInstall(Status.Snapshot().Count, paused, stop is not null, Application.OpenForms.Count + (GetActiveWindow() != IntPtr.Zero ? 1 : 0), menu.Visible)) { completion.TrySetResult(false); return; }
                            menu.Enabled = false;
                            paused = true; Refresh();
                            await Stop();
                            if (token.IsCancellationRequested || Status.Snapshot().Count != 0 || Application.OpenForms.Count != 0 || GetActiveWindow() != IntPtr.Zero) { Start(); menu.Enabled = true; completion.TrySetResult(false); return; }
                            try
                            {
                                // 待ち受け停止中は HotSync が始まらない。
                                UpdateInstaller.Install(AppContext.BaseDirectory, staged.Folder);
                            }
                            catch (Exception ex) { Log($"更新の入れ替え: 失敗 {ex.Message}"); Start(); menu.Enabled = true; completion.TrySetResult(false); return; }
                            pipeStop.Cancel();
                            try { await pipeWorker; } catch (Exception ex) { Log(ex.ToString()); }
                            trayIcon.Visible = false;
                            releaseRequest.Set();
                            await Task.Run(() => released.WaitOne());
                            try { await Task.Run(() => new UpdateHandover().Run(AppContext.BaseDirectory, staged, currentTag!, Log)); }
                            finally { completion.TrySetResult(true); Application.ExitThread(); }
                        }
                        catch (Exception ex) { Log(ex.ToString()); completion.TrySetException(ex); }
                    }));
                }
                catch (InvalidOperationException) { completion.TrySetResult(false); }
                return await completion.Task;
            }
            void StartUpdates()
            {
                if (currentTag is null || !updateEnabled) return;
                updateStop = new CancellationTokenSource();
                var token = updateStop.Token;
                updateWorker = Task.Run(async () =>
                {
                    try { await new UpdateScheduler(new UpdateFetcher()).RunAsync(currentTag, () => updateEnabled, () => settings.FailedTag, TryInstall, Log, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                    catch (Exception ex) { Log(ex.ToString()); }
                });
            }
            bool Handover()
            {
                try
                {
                    var result = RenameHandover.Run(new HandoverEnvironment());
                    foreach (string line in result.Actions) Log(line);
                    foreach (string line in result.Warnings) { Log(line); Notify("PiloSync", result.OldRunning ? "古い PalmSync が動いています。終了してから『再開』を押してください" : line); }
                    if (result.OldRunning) return false;
                }
                catch (Exception ex) { Log(ex.ToString()); Notify("PiloSync", ex.Message); }
                // Only a running old PalmSync blocks listening; other failures are logged as before.
                try { StartupCommand.MigrateRegistration(new StartupEnvironment()); } catch (Exception ex) { Log(ex.ToString()); }
                return true;
            }
            void Start() { if (!Handover()) { paused = true; Refresh(); return; } paused = false; Refresh(); stop = new(); worker = ListenerSupervisor.Run(t => listen(Log, t), Log, stop.Token); }
            async Task Stop() { stop?.Cancel(); await worker; stop?.Dispose(); stop = null; }
            void Open(string path) { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { Log(ex.ToString()); Notify("開けません", ex.Message); } }
            AddDeviceActions(menu, () => paused, Refresh);
            menu.Items.Insert(1, new ToolStripMenuItem("端末にインストール…", null, (_, _) => SelectInstall([])));
            menu.Items.Insert(2, new ToolStripMenuItem("HotSync の時刻…", null, (_, _) => SelectSchedule()));
            string? launcherVersion = Environment.GetEnvironmentVariable("PILOSYNC_LAUNCHER_VERSION");
            string versionText = launcherVersion is null ? $"PiloSync {Version}" : $"PiloSync {launcherVersion}" + (launcherVersion == Version ? "" : $"（cli {Version}）");
            menu.Items.Insert(0, new ToolStripSeparator());
            menu.Items.Insert(0, new ToolStripMenuItem(versionText) { Enabled = false });
            Log($"{versionText} を起動");
            menu.Items.Add("エクスプローラーの『送る』に追加", null, (_, _) =>
            {
                try { Notify("送るに追加", SendToShortcut.Create()); }
                catch (Exception ex) { Log(ex.ToString()); Notify("送るに追加できません", ex.Message); }
            });
            var addAssociation = new ToolStripMenuItem("prc・pdb を開くアプリに PiloSync を追加");
            var removeAssociation = new ToolStripMenuItem("prc・pdb を開くアプリから PiloSync の登録を外す");
            menu.Items.Add(addAssociation); menu.Items.Add(removeAssociation);
            menu.Opening += (_, _) =>
            {
                try
                {
                    using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes");
                    bool registered = root is not null && new PalmFileRegistration(root).IsRegistered();
                    addAssociation.Visible = !registered; removeAssociation.Visible = registered;
                }
                catch (Exception ex) { Log(ex.ToString()); Notify("PiloSync", ex.Message); }
            };
            addAssociation.Click += (_, _) =>
            {
                try
                {
                    string executable = Path.Combine(AppContext.BaseDirectory, "PiloSync.exe");
                    if (!File.Exists(executable)) throw new FileNotFoundException("同じフォルダに PiloSync.exe が見つかりません。", executable);
                    using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes", true) ?? throw new InvalidOperationException("レジストリを開けません。");
                    new PalmFileRegistration(root).Register(executable); PalmFileRegistration.NotifyChanged();
                    Notify("PiloSync", "登録しました。prc を右クリック →『プログラムから開く』→『別のプログラムを選択』で PiloSync を選び、『常に使う』を押してください。");
                }
                catch (Exception ex) { Log(ex.ToString()); Notify("登録できません", ex.Message); }
            };
            removeAssociation.Click += (_, _) =>
            {
                try
                {
                    using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes", true) ?? throw new InvalidOperationException("レジストリを開けません。");
                    new PalmFileRegistration(root).Unregister(); PalmFileRegistration.NotifyChanged();
                    Notify("PiloSync", "開くアプリから PiloSync の登録を外しました。");
                }
                catch (Exception ex) { Log(ex.ToString()); Notify("登録を外せません", ex.Message); }
            };
            pipeWorker = Task.Run(() => InstallPipe.Listen(paths => control.BeginInvoke((Action)(() => SelectInstall(paths))), Log, pipeStop.Token));
            if (installFiles.Length > 0) control.BeginInvoke((Action)(() => SelectInstall(installFiles)));
            menu.Items.Add("ログを開く", null, (_, _) => { Log("ログを開きます"); Open(TodayLog); });
            menu.Items.Add("保存先のフォルダを開く", null, (_, _) => { var path = new PalmRegistry().DataPath; if (!string.IsNullOrWhiteSpace(path)) Open(path); else Notify("保存先", "Core\\Path が設定されていません。"); });
            var startup = new ToolStripMenuItem("サインイン時に自動で起動する"); menu.Items.Add(startup);
            if (currentTag is not null)
            {
                var autoUpdate = new ToolStripMenuItem("自動アップデート") { CheckOnClick = true, Checked = updateEnabled, ToolTipText = "GitHub の Releases から新しい版を入れます" };
                menu.ShowItemToolTips = true;
                menu.Items.Add(autoUpdate);
                autoUpdate.CheckedChanged += (_, _) =>
                {
                    updateEnabled = autoUpdate.Checked;
                    settings.SetEnabled(updateEnabled);
                    Log($"自動アップデート: {(updateEnabled ? "オン" : "オフ")}");
                    if (updateEnabled) StartUpdates(); else updateStop?.Cancel();
                };
            }
            var environment = new StartupEnvironment();
            void RefreshStartup()
            {
                try { startup.Checked = StartupCommand.IsRegistered(environment.GetPiloSync(), [StartupEnvironment.LauncherExecutable, StartupEnvironment.CurrentExecutable]); }
                catch (Exception ex) { Log(ex.ToString()); Notify("PiloSync", $"自動起動の設定に失敗しました：{ex.Message}"); }
            }
            menu.Opening += (_, _) => RefreshStartup();
            startup.Click += async (_, _) =>
            {
                bool enable = !startup.Checked; startup.Enabled = false;
                try
                {
                    string message = await Task.Run(() =>
                    {
                        if (!enable) { StartupCommand.DisableRegistration(environment); return "サインイン時の自動起動を解除しました。純正 HotSync の自動起動に戻すには pilosync-cli.exe uninstall-startup を実行してください。"; }
                        var remaining = StartupCommand.Install(environment, TextWriter.Null);
                        return remaining.Count > 0 ? "純正 HotSync の自動起動を止められませんでした。タスクマネージャーの『スタートアップ アプリ』で無効にしてください。" : "サインイン時の自動起動を登録しました";
                    });
                    Notify("PiloSync", message);
                }
                catch (Exception ex) { Log(ex.ToString()); Notify("PiloSync", $"自動起動の設定に失敗しました：{ex.Message}"); }
                finally { RefreshStartup(); startup.Enabled = true; }
            };
            var pause = menu.Items.Add("一時停止");
            pause.Click += async (_, _) => { menu.Enabled = false; try { if (stop is null) { Start(); pause.Text = paused ? "再開" : "一時停止"; } else { paused = true; Refresh(); await Stop(); pause.Text = "再開"; } } catch (Exception ex) { Crash(ex); } finally { menu.Enabled = true; } };
            var exit = menu.Items.Add("終了"); exit.Click += async (_, _) => { menu.Enabled = false; paused = true; Refresh(); await Stop(); trayIcon.Visible = false; Application.ExitThread(); };
            try
            {
                for (int i = 0; i < TrayIcons.FrameCount; i++) frames.Add(TrayIcons.Create(size, i, false));
                Start();
                if (!paused && stop is not null) { try { UpdateHandover.SignalReady(); } catch (Exception ex) { Log(ex.ToString()); } }
                try
                {
                    if (UpdateHandover.ReadUpdatedFrom() is not null)
                    {
                        UpdateHandover.ClearUpdatedFrom();
                        string message = $"PiloSync {Version} に更新しました";
                        Log(message); Notify("PiloSync", message);
                    }
                }
                catch (Exception ex) { Log(ex.ToString()); }
                StartUpdates();
                pause.Text = paused ? "再開" : "一時停止";
                foreach (var process in Process.GetProcessesByName("HotSync")) { process.Dispose(); Notify("PiloSync", "純正 HotSync Manager が起動中です。ポートを取り合うので終了してください"); break; }
                Application.Run();
            }
            catch (Exception ex) { Crash(ex); result = 1; }
            finally { updateStop?.Cancel(); pipeStop.Cancel(); try { pipeWorker.GetAwaiter().GetResult(); } catch (Exception ex) { Crash(ex); } installForm?.Close(); activation.Unregister(null); timer.Stop(); stop?.Cancel(); try { worker.GetAwaiter().GetResult(); } catch (Exception ex) { Crash(ex); } stop?.Dispose(); refresh = null; icon = null; dispatcher = null; trayIcon.Visible = false; trayIcon.Icon = null; foreach (var item in frames) item.Dispose(); finished.Set(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        bool mutexReleased = WaitHandle.WaitAny([releaseRequest, finished]) == 0;
        if (mutexReleased) { mutex.ReleaseMutex(); released.Set(); }
        thread.Join();
        if (!mutexReleased) mutex.ReleaseMutex();
        return result;
    }
}
