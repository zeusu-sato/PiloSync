using System.Windows.Forms;
using PiloSync.Core;

namespace PiloSync.Cli;

public static partial class TrayHost
{
    private static Form? scheduleForm;
    private static void SelectSchedule()
    {
        try
        {
            if (scheduleForm is not null) { if (scheduleForm.WindowState == FormWindowState.Minimized) scheduleForm.WindowState = FormWindowState.Normal; scheduleForm.Activate(); return; }
            string path = HotSyncSchedule.DefaultPath;
            string? dataPath = new PalmRegistry().DataPath;
            string[] accounts = !string.IsNullOrWhiteSpace(dataPath) && File.Exists(Path.Combine(dataPath, "users.dat"))
                ? new AccountStore(dataPath).Data.Users.Select(u => u.Name).ToArray() : [];
            var settings = HotSyncSchedule.LoadSettings(path);
            if (settings.Legacy) settings = HotSyncSchedule.Update(path, current => current.MigrateLegacy(accounts));
            DayOfWeek[] days = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];
            var form = new Form { Text = "HotSync の時刻", ClientSize = new(610, 236), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterScreen };
            var users = new CheckedListBox { Location = new(12, 12), Size = new(180, 170), CheckOnClick = true };
            var grid = new DataGridView { Location = new(202, 12), Size = new(396, 170), AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeColumns = false, AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, ScrollBars = ScrollBars.Vertical, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "時刻", ReadOnly = true, FillWeight = 150, SortMode = DataGridViewColumnSortMode.NotSortable });
            foreach (string name in new[] { "月", "火", "水", "木", "金", "土", "日" })
                grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = name, SortMode = DataGridViewColumnSortMode.NotSortable });
            var picker = new DateTimePicker { Location = new(202, 196), Size = new(96, 27), Format = DateTimePickerFormat.Custom, CustomFormat = "H:mm", ShowUpDown = true, Value = DateTime.Today };
            var add = new Button { Text = "追加", Location = new(308, 194), Size = new(60, 30) };
            var remove = new Button { Text = "削除", Location = new(378, 194), Size = new(60, 30), Enabled = false };
            var tip = new ToolTip(); bool refreshing = false; bool tipped = false; int hoverIndex = -1;
            form.Controls.AddRange([users, grid, picker, add, remove]); form.AcceptButton = add;
            int? SelectedMinutes() => grid.SelectedRows.Count > 0 ? (int?)grid.SelectedRows[0].Tag : null;
            void Reload(ScheduleSettings value, int? selected = null)
            {
                refreshing = true;
                try
                {
                    settings = value;
                    int userIndex = users.SelectedIndex;
                    users.Items.Clear();
                    foreach (string name in accounts)
                        users.Items.Add(name, value.StateFor(name) switch { ScheduleDeviceState.On => CheckState.Checked, ScheduleDeviceState.Inherit => CheckState.Indeterminate, _ => CheckState.Unchecked });
                    if (userIndex >= 0 && userIndex < users.Items.Count) users.SelectedIndex = userIndex;
                    grid.Rows.Clear();
                    foreach (var time in value.Times)
                    {
                        int row = grid.Rows.Add(); grid.Rows[row].Tag = time.Minutes;
                        grid.Rows[row].Cells[0].Value = HotSyncSchedule.Format(time.Minutes);
                        for (int i = 0; i < days.Length; i++) grid.Rows[row].Cells[i + 1].Value = time.Days.Contains(days[i]);
                    }
                    grid.ClearSelection();
                    foreach (DataGridViewRow row in grid.Rows) if (row.Tag is int valueMinutes && valueMinutes == selected) { row.Selected = true; grid.CurrentCell = row.Cells[0]; break; }
                    remove.Enabled = grid.SelectedRows.Count > 0;
                    add.Enabled = days.Any(day => value.Times.Count(t => t.Days.Contains(day)) < HotSyncSchedule.MaxTimes);
                }
                finally { refreshing = false; }
            }
            void Save(Func<ScheduleSettings, ScheduleSettings> change, int? selected)
            {
                try { Reload(HotSyncSchedule.Update(path, current => change(current.MigrateLegacy(accounts))), selected); }
                catch (Exception ex)
                {
                    Log(ex.ToString()); MessageBox.Show(form, ex.Message, "HotSync の時刻", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    Reload(HotSyncSchedule.LoadSettings(path), selected);
                }
            }
            users.ItemCheck += (_, e) =>
            {
                if (refreshing) return;
                string name = (string)users.Items[e.Index];
                var state = e.CurrentValue == CheckState.Checked ? ScheduleDeviceState.Off : ScheduleDeviceState.On;
                e.NewValue = e.CurrentValue;
                int? selected = SelectedMinutes();
                form.BeginInvoke((Action)(() => Save(current => current with { Devices = current.Devices.Where(d => !string.Equals(d.Name, name, StringComparison.Ordinal)).Append(new ScheduleDevice(name, state)).ToArray() }, selected)));
            };
            users.MouseMove += (_, e) =>
            {
                int index = users.IndexFromPoint(e.Location);
                if (index == hoverIndex) return;
                hoverIndex = index; tip.Hide(users);
                if (index >= 0 && users.GetItemCheckState(index) == CheckState.Indeterminate) tip.Show("次の HotSync で端末の設定に合わせます", users, e.X, e.Y + 20, 3000);
            };
            users.MouseLeave += (_, _) => { hoverIndex = -1; tip.Hide(users); };
            grid.SelectionChanged += (_, _) => { if (!refreshing) remove.Enabled = grid.SelectedRows.Count > 0; };
            grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellValueChanged += (_, e) =>
            {
                if (refreshing || e.RowIndex < 0 || e.ColumnIndex < 1) return;
                int minutes = (int)grid.Rows[e.RowIndex].Tag!; DayOfWeek day = days[e.ColumnIndex - 1];
                bool enabled = grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value is true;
                int column = e.ColumnIndex;
                form.BeginInvoke((Action)(() =>
                {
                    bool full = false;
                    Save(current =>
                    {
                        var row = current.Times.FirstOrDefault(t => t.Minutes == minutes);
                        if (row is null) return current;
                        if (enabled && !row.Days.Contains(day) && current.Times.Count(t => t.Days.Contains(day)) >= HotSyncSchedule.MaxTimes) { full = true; return current; }
                        var updated = enabled ? row.Days.Append(day).Distinct().ToArray() : row.Days.Where(d => d != day).ToArray();
                        return current with { Times = current.Times.Select(t => t.Minutes == minutes ? t with { Days = updated } : t).ToArray() };
                    }, minutes);
                    if (full)
                    {
                        var row = grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Tag is int rowMinutes && rowMinutes == minutes);
                        if (row is not null) { var rect = grid.GetCellDisplayRectangle(column, row.Index, true); tip.Show("1 日 8 個まで", grid, rect.Left, rect.Bottom, 3000); }
                    }
                }));
            };
            add.Click += (_, _) =>
            {
                int minutes = picker.Value.Hour * 60 + picker.Value.Minute;
                if (settings.Times.Any(t => t.Minutes == minutes)) { Reload(settings, minutes); return; }
                Save(current =>
                {
                    if (current.Times.Any(t => t.Minutes == minutes)) return current;
                    var available = days.Where(day => current.Times.Count(t => t.Days.Contains(day)) < HotSyncSchedule.MaxTimes).ToArray();
                    return available.Length == 0 ? current : current with { Times = current.Times.Append(new ScheduleTime(minutes, available)).ToArray() };
                }, minutes);
            };
            remove.Click += (_, _) => { int? selected = SelectedMinutes(); if (selected is int minutes) Save(current => current with { Times = current.Times.Where(t => t.Minutes != minutes).ToArray() }, null); };
            form.MouseMove += (_, e) =>
            {
                bool over = !add.Enabled && add.Bounds.Contains(e.Location);
                if (over == tipped) return;
                tipped = over;
                if (over) tip.Show("1 日 8 個まで", form, add.Right, add.Bottom); else tip.Hide(form);
            };
            scheduleForm = form;
            form.FormClosed += (_, _) => { scheduleForm = null; tip.Dispose(); form.Dispose(); };
            form.Show(); Reload(settings);
        }
        catch (Exception ex) { Log(ex.ToString()); Notify("HotSync の時刻", ex.Message); }
    }
}

