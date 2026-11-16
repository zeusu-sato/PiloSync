using System.Windows.Forms;

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
}
