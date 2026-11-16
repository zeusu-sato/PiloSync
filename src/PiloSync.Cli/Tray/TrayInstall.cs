using System.Windows.Forms;
using PiloSync.Core;

namespace PiloSync.Cli;

public static partial class TrayHost
{
    private static Form? installForm;
    private static Action<IEnumerable<string>>? addInstallFiles;
    private static void SelectInstall(IEnumerable<string> initial)
    {
        try
        {
            if (installForm is not null) { addInstallFiles?.Invoke(initial); if (installForm.WindowState == FormWindowState.Minimized) installForm.WindowState = FormWindowState.Normal; installForm.Activate(); return; }
            string? path = new PalmRegistry().DataPath;
            if (string.IsNullOrWhiteSpace(path)) { Notify("端末にインストール", "Core\\Path が設定されていません。"); return; }
            var store = new AccountStore(path); var accounts = store.Data.Users.ToArray();
            if (accounts.Length == 0) { Notify("端末にインストール", "アカウントがありません。"); return; }
            var form = new Form { Text = "端末にインストール", ClientSize = new(580, 610), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterScreen, AllowDrop = true };
            var users = new CheckedListBox { Location = new(12, 32), Size = new(556, 100), CheckOnClick = true };
            foreach (var account in accounts) users.Items.Add(account.Name, accounts.Length == 1);
            var files = new ListBox { Location = new(12, 164), Size = new(556, 170), HorizontalScrollbar = true, SelectionMode = SelectionMode.MultiExtended };
            var add = new Button { Text = "追加…", Location = new(12, 342), Size = new(80, 30) };
            var remove = new Button { Text = "削除", Location = new(102, 342), Size = new(80, 30) };
            var pending = new ListBox { Location = new(12, 402), Size = new(556, 110), HorizontalScrollbar = true, SelectionMode = SelectionMode.MultiExtended };
            var cancelPending = new Button { Text = "予約を取り消す", Location = new(12, 520), Size = new(120, 30), Enabled = false };
            var ok = new Button { Text = "インストール", Location = new(352, 568), Size = new(104, 30) };
            var cancel = new Button { Text = "キャンセル", Location = new(466, 568), Size = new(102, 30) };
            form.Controls.AddRange([new Label { Text = "アカウント（複数選択可）", Location = new(12, 10), AutoSize = true }, users, new Label { Text = "ファイル（ここにドラッグ＆ドロップできます）", Location = new(12, 142), AutoSize = true }, files, add, remove, new Label { Text = "インストール予定（次の HotSync で入るもの）", Location = new(12, 380), AutoSize = true }, pending, cancelPending, ok, cancel]);
            form.AcceptButton = ok; form.CancelButton = cancel;
            IReadOnlyList<PendingInstall> pendingItems = [];
            void RefreshPending()
            {
                pending.Items.Clear();
                pendingItems = InstallQueue.Pending(store, users.CheckedIndices.Cast<int>().Select(i => accounts[i]));
                foreach (var item in pendingItems)
                    pending.Items.Add($"{item.Account.Name}：{item.FileName}（{item.Size:N0} バイト）");
            }
            pending.SelectedIndexChanged += (_, _) => cancelPending.Enabled = pending.SelectedIndices.Count > 0;
            cancelPending.Click += (_, _) =>
            {
                var selected = pending.SelectedIndices.Cast<int>().Select(i => pendingItems[i]).ToArray();
                var names = selected.Take(10).Select(i => $"{i.Account.Name}：{i.FileName}").ToList();
                if (selected.Length > 10) names.Add($"ほか {selected.Length - 10} 件");
                if (MessageBox.Show(form, "次のファイルの予約を取り消しますか？（ファイルはごみ箱に移します）" + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, names), "予約の取り消し", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                var results = InstallQueue.Cancel(store, selected, path => Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException));
                foreach (var result in results.Where(r => r.Cancelled)) Log($"インストール予約 {result.Item.Account.Name}: {result.Item.FileName}: 取り消し");
                RefreshPending();
                var errors = results.Where(r => r.Error is not null).Select(r => $"{r.Item.Account.Name}: {r.Item.FileName}: {r.Error}").ToArray();
                if (errors.Length > 0) MessageBox.Show(form, string.Join(Environment.NewLine, errors), "取り消せないファイル", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            users.ItemCheck += (_, _) => form.BeginInvoke((Action)RefreshPending);
            void AddFiles(IEnumerable<string> paths)
            {
                var warnings = new List<string>();
                foreach (string file in paths)
                {
                    string? error = InstallQueue.Check(file);
                    if (error is not null) { warnings.Add($"{file}: {error}"); continue; }
                    string full = Path.GetFullPath(file);
                    if (!files.Items.Cast<string>().Contains(full, StringComparer.OrdinalIgnoreCase)) files.Items.Add(full);
                }
                if (warnings.Count > 0) MessageBox.Show(form, string.Join(Environment.NewLine, warnings), "追加できないファイル", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            add.Click += (_, _) => { using var dialog = new OpenFileDialog { Multiselect = true, Filter = "Palm ファイル (*.prc;*.pdb;*.pqa)|*.prc;*.pdb;*.pqa", CheckFileExists = true }; if (dialog.ShowDialog(form) == DialogResult.OK) AddFiles(dialog.FileNames); };
            remove.Click += (_, _) => { foreach (int index in files.SelectedIndices.Cast<int>().OrderDescending().ToArray()) files.Items.RemoveAt(index); };
            cancel.Click += (_, _) => form.Close();
            form.DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
            form.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) AddFiles(paths); };
            ok.Click += (_, _) =>
            {
                if (users.CheckedIndices.Count == 0 || files.Items.Count == 0) { MessageBox.Show(form, "アカウントとファイルを選んでください。", "端末にインストール"); return; }
                var results = InstallQueue.Add(store, users.CheckedIndices.Cast<int>().Select(i => accounts[i]), files.Items.Cast<string>(), (account, target) => MessageBox.Show(form, $"{account.Name} の {Path.GetFileName(target)} を上書きしますか？", "上書きの確認", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes);
                RefreshPending();
                foreach (var result in results) Log($"インストール予約 {result.Account.Name}: {result.Source}: {(result.Added ? "追加" : result.Skipped ? "スキップ" : result.Error)}");
                var errors = results.Where(r => r.Error is not null).Select(r => $"{r.Account.Name}: {r.Source}: {r.Error}").ToArray();
                if (errors.Length > 0) MessageBox.Show(form, string.Join(Environment.NewLine, errors), "追加できないファイル", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                var added = results.Where(r => r.Added).ToArray();
                if (added.Length > 0) Notify("PiloSync", $"次の HotSync で{string.Join("、", added.Select(r => r.Account.Name).Distinct())}に入ります（{added.Length} 件）");
                form.Close();
            };
            installForm = form; addInstallFiles = AddFiles;
            form.FormClosed += (_, _) => { installForm = null; addInstallFiles = null; form.Dispose(); };
            form.Show(); RefreshPending(); AddFiles(initial);
        }
        catch (Exception ex) { Log(ex.ToString()); Notify("端末にインストール", ex.Message); }
    }
}
