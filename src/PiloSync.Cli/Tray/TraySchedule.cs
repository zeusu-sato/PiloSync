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
            var times = HotSyncSchedule.Load(path).ToList();
            var form = new Form { Text = "HotSync の時刻", ClientSize = new(260, 236), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterScreen };
            var list = new ListBox { Location = new(12, 12), Size = new(236, 170) };
            var picker = new DateTimePicker { Location = new(12, 196), Size = new(96, 27), Format = DateTimePickerFormat.Custom, CustomFormat = "H:mm", ShowUpDown = true, Value = DateTime.Today };
            var add = new Button { Text = "追加", Location = new(118, 194), Size = new(60, 30) };
            var remove = new Button { Text = "削除", Location = new(188, 194), Size = new(60, 30), Enabled = false };
            var tip = new ToolTip(); bool tipped = false;
            form.Controls.AddRange([list, picker, add, remove]); form.AcceptButton = add;
            void Reload(int selected = -1)
            {
                list.Items.Clear();
                foreach (int minutes in times) list.Items.Add(HotSyncSchedule.Format(minutes));
                list.SelectedIndex = selected;
                remove.Enabled = list.SelectedIndex >= 0;
                add.Enabled = times.Count < HotSyncSchedule.MaxTimes;
            }
            bool Save(List<int> next)
            {
                try { HotSyncSchedule.Save(path, next); return true; }
                catch (Exception ex) { Log(ex.ToString()); MessageBox.Show(form, ex.Message, "HotSync の時刻", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
            }
            list.SelectedIndexChanged += (_, _) => remove.Enabled = list.SelectedIndex >= 0;
            add.Click += (_, _) =>
            {
                int minutes = picker.Value.Hour * 60 + picker.Value.Minute;
                int index = times.IndexOf(minutes);
                if (index >= 0) { list.SelectedIndex = index; return; }
                var next = times.Append(minutes).Order().ToList();
                if (!Save(next)) return;
                times = next; Reload(times.IndexOf(minutes));
            };
            remove.Click += (_, _) =>
            {
                int selected = list.SelectedIndex;
                if (selected < 0) return;
                var next = times.Where((_, i) => i != selected).ToList();
                if (!Save(next)) return;
                times = next; Reload();
            };
            form.MouseMove += (_, e) =>
            {
                bool over = !add.Enabled && add.Bounds.Contains(e.Location);
                if (over == tipped) return;
                tipped = over;
                if (over) tip.Show("1 日 8 個まで", form, add.Right, add.Bottom); else tip.Hide(form);
            };
            scheduleForm = form;
            form.FormClosed += (_, _) => { scheduleForm = null; tip.Dispose(); form.Dispose(); };
            form.Show(); Reload();
        }
        catch (Exception ex) { Log(ex.ToString()); Notify("HotSync の時刻", ex.Message); }
    }
}
