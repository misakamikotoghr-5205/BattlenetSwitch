using System.Drawing;
using WinControl = System.Windows.Forms.Control;

namespace QuickSwitch;

internal sealed class MainWindow : Form
{
    private static readonly Color Background = Color.FromArgb(244, 247, 251);
    private static readonly Color Ink = Color.FromArgb(26, 40, 61);
    private static readonly Color Secondary = Color.FromArgb(68, 86, 110);
    private static readonly Color Accent = Color.FromArgb(29, 91, 193);
    private static readonly Dictionary<string, string> RegionNames = new()
    {
        ["CN"] = "国服", ["US"] = "美服", ["EU"] = "欧服", ["KR"] = "亚服", ["TW"] = "台服"
    };

    private readonly SwitchEngine engine;
    private readonly ILauncher launcher;
    private readonly ComboBox region = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 145 };
    private readonly Label count = new() { AutoSize = true, ForeColor = Secondary, Padding = new(8, 7, 0, 0) };
    private readonly Label status = new() { Dock = DockStyle.Fill, ForeColor = Secondary, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ListView list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false,
        MultiSelect = false, BorderStyle = BorderStyle.FixedSingle
    };

    private Button switchButton = null!;
    private Button renameButton = null!;
    private Button removeButton = null!;
    private TableLayoutPanel layout = null!;
    private bool busy;
    private string? requestedProfile;

    public MainWindow(SwitchEngine engine, ILauncher launcher)
    {
        this.engine = engine;
        this.launcher = launcher;
        Text = "战网一键切号";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(752, 474);
        MinimumSize = new Size(726, 438);
        BackColor = Background;
        ForeColor = Ink;
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
        Build();
        Shown += (_, _) =>
        {
            try
            {
                var recovered = engine.Recover();
                engine.ObserveLoginSlots();
                RefreshProfiles();
                if (recovered) status.Text = "已恢复上次中断前的战网配置。";
            }
            catch (Exception ex)
            {
                status.Text = "读取本地状态失败。";
                ShowError(ex);
                TryRefresh();
            }
        };
        FormClosing += (_, e) => e.Cancel = busy;
    }

    private void Build()
    {
        SuspendLayout();
        layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 15, 24, 12), RowCount = 6, ColumnCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 47f, 34f, 41f })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        Controls.Add(layout);

        var title = new Label
        {
            Text = "战网一键切号", Font = new Font(Font.FontFamily, 20F, FontStyle.Bold),
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft
        };
        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = "在战网里勾选「保持登录」。每个号保存一次，之后双击账号切换。",
            Dock = DockStyle.Fill, ForeColor = Secondary, TextAlign = ContentAlignment.MiddleLeft
        }, 0, 1);

        var toolbar = Bar();
        toolbar.Controls.Add(new Label { AutoSize = true, Text = "登录区服", Padding = new(0, 8, 2, 0) });
        foreach (var entry in RegionNames) region.Items.Add(entry.Value + "  " + entry.Key);
        region.SelectedIndex = 0;
        region.Margin = new Padding(0, 4, 9, 0);
        toolbar.Controls.Add(region);
        toolbar.Controls.Add(Button("登录新账号", async () => await Run("正在准备战网登录…", () =>
        {
            engine.PrepareLogin(RequireExe(), SelectedRegion());
            return "请在战网完成登录并勾选「保持登录」，然后回来保存快照。";
        })));
        toolbar.Controls.Add(Button("打开战网", async () => await Run("正在打开战网…", () =>
        {
            engine.Recover();
            launcher.Launch(RequireExe(), null);
            return "战网已启动。";
        })));
        toolbar.Controls.Add(Button("设置路径", () => { ChooseExe(); return Task.CompletedTask; }));
        toolbar.Controls.Add(count);
        layout.Controls.Add(toolbar, 0, 2);

        list.Columns.Add("账号或备注");
        list.Columns.Add("登录区服");
        list.Columns.Add("快照保存时间");
        list.Columns.Add("本机凭据");
        list.Resize += (_, _) =>
        {
            var available = Math.Max(0, list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
            var widths = new[] { 39, 15, 25, 21 };
            for (var i = 0; i < widths.Length; i++) list.Columns[i].Width = available * widths[i] / 100;
        };
        list.SelectedIndexChanged += (_, _) => UpdateButtons();
        list.DoubleClick += async (_, _) => await SwitchSelected();
        list.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await SwitchSelected();
        };
        layout.Controls.Add(list, 0, 3);

        var actions = Bar();
        actions.Padding = new Padding(0, 7, 0, 0);
        switchButton = Button("切换到选中账号", SwitchSelected, primary: true);
        actions.Controls.Add(switchButton);
        actions.Controls.Add(Button("保存当前快照", async () => await Run("正在保存快照，战网可能会重新启动…", () =>
        {
            var saved = engine.SaveCurrent(RequireExe(), SelectedRegion());
            requestedProfile = saved.Id;
            return "已保存「" + saved.Label + "」的快照。";
        })));
        renameButton = Button("改备注", RenameSelected);
        actions.Controls.Add(renameButton);
        removeButton = Button("删除快照", RemoveSelected);
        actions.Controls.Add(removeButton);
        layout.Controls.Add(actions, 0, 4);
        status.Text = "还没有快照时，可先保存当前账号，或点击「登录新账号」。";
        layout.Controls.Add(status, 0, 5);
        ResumeLayout(true);
    }

    private static FlowLayoutPanel Bar() => new()
    {
        Dock = DockStyle.Fill, AutoSize = false, WrapContents = false, Margin = Padding.Empty
    };

    private static Button Button(string text, Func<Task> action, bool primary = false)
    {
        var button = new Button
        {
            Text = text, AutoSize = true, MinimumSize = new Size(82, 33), Padding = new Padding(8, 1, 8, 1),
            FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Color.White,
            ForeColor = primary ? Color.White : Ink, Margin = new Padding(0, 1, 8, 3), Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(208, 218, 233);
        button.Click += async (_, _) => await action();
        return button;
    }

    private Profile? Selected() => list.SelectedItems.Count == 1 ? list.SelectedItems[0].Tag as Profile : null;
    private string SelectedRegion()
    {
        if (InvokeRequired) return (string)Invoke(new Func<string>(SelectedRegion));
        return LauncherConfig.Regions[Math.Max(0, region.SelectedIndex)];
    }

    private string RequireExe()
    {
        if (InvokeRequired) return (string)Invoke(new Func<string>(RequireExe));
        return WindowsLauncher.FindExe(engine) ?? ChooseExe() ??
            throw new OperationCanceledException("已取消选择战网路径。");
    }

    private string? ChooseExe()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择 Battle.net.exe 或 Battle.net Launcher.exe",
            Filter = "战网启动程序|Battle.net.exe;Battle.net Launcher.exe|可执行文件|*.exe",
            CheckFileExists = true, RestoreDirectory = true
        };
        var known = WindowsLauncher.FindExe(engine);
        if (known is not null) dialog.InitialDirectory = Path.GetDirectoryName(known);
        if (dialog.ShowDialog(this) != DialogResult.OK) return null;
        if (!WindowsLauncher.IsBattleNetExe(dialog.FileName))
        {
            ShowError(new InvalidOperationException("请选择 Battle.net.exe 或 Battle.net Launcher.exe。"));
            return null;
        }
        try { engine.SetExe(dialog.FileName); }
        catch (Exception ex) { ShowError(ex); return null; }
        status.Text = "已保存战网程序路径。";
        return dialog.FileName;
    }

    private void RefreshProfiles()
    {
        var selectedId = requestedProfile ?? Selected()?.Id;
        requestedProfile = null;
        var profiles = engine.Profiles.OrderBy(p => p.Region).ThenByDescending(p => p.SavedUtc).ToArray();
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            foreach (var p in profiles)
            {
                var item = new ListViewItem(p.Label) { Tag = p };
                item.SubItems.Add(RegionNames.GetValueOrDefault(p.Region, p.Region));
                item.SubItems.Add(p.SavedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                item.SubItems.Add(p.Auth is null ? "由战网保管" : "已随快照保存");
                list.Items.Add(item);
                if (p.Id == selectedId) item.Selected = true;
            }
            if (list.SelectedItems.Count == 0 && list.Items.Count > 0) list.Items[0].Selected = true;
            count.Text = profiles.Length + " 个快照";
        }
        finally { list.EndUpdate(); }
        UpdateButtons();
    }

    private void TryRefresh()
    {
        try { RefreshProfiles(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void UpdateButtons()
    {
        if (switchButton is null || renameButton is null || removeButton is null) return;
        switchButton.Enabled = renameButton.Enabled = removeButton.Enabled = !busy && Selected() is not null;
    }

    private Task SwitchSelected() => Selected() is { } p
        ? Run("正在切换到「" + p.Label + "」…", () =>
        {
            engine.SwitchTo(RequireExe(), p);
            return "已载入「" + p.Label + "」并启动战网。若战网要求验证，请在客户端完成。";
        })
        : Task.CompletedTask;

    private async Task RenameSelected()
    {
        if (Selected() is not { } p) return;
        using var dialog = new Form
        {
            Text = "修改账号备注", StartPosition = FormStartPosition.CenterParent, ClientSize = new(360, 132),
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, Font = Font,
            FormBorderStyle = FormBorderStyle.FixedDialog, BackColor = Background
        };
        using var input = new TextBox { Text = p.Label, Left = 16, Top = 38, Width = 328, MaxLength = 60 };
        input.SelectAll();
        var ok = new Button { Text = "保存", DialogResult = DialogResult.OK, Bounds = new(174, 83, 80, 31) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Bounds = new(264, 83, 80, 31) };
        dialog.Controls.Add(new Label { Text = "账号备注", AutoSize = true, Left = 16, Top = 14 });
        dialog.Controls.AddRange([input, ok, cancel]);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) == DialogResult.OK)
            await Run("正在保存备注…", () => { engine.Rename(p.Id, input.Text); return "已保存备注。"; });
    }

    private async Task RemoveSelected()
    {
        if (Selected() is not { } p) return;
        if (MessageBox.Show(this, "删除「" + p.Label + "」在切号工具里的快照？", Text,
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await Run("正在删除快照…", () => { engine.Remove(p.Id); return "已删除本地快照。"; });
    }

    private async Task Run(string progress, Func<string> action)
    {
        if (busy) return;
        busy = true;
        status.Text = progress;
        foreach (WinControl control in layout.Controls)
            if (control is FlowLayoutPanel || control == list) control.Enabled = false;
        try
        {
            status.Text = await Task.Run(action);
            RefreshProfiles();
        }
        catch (OperationCanceledException ex) { status.Text = ex.Message; }
        catch (Exception ex)
        {
            status.Text = "操作未完成。";
            ShowError(ex);
            TryRefresh();
        }
        finally
        {
            busy = false;
            foreach (WinControl control in layout.Controls)
                if (control is FlowLayoutPanel || control == list) control.Enabled = true;
            UpdateButtons();
        }
    }

    private void ShowError(Exception ex) =>
        MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
