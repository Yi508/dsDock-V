using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace DsDock.Setup;

/// <summary>安装向导窗口：选择安装位置 + 两个选项 + 安装/取消。</summary>
internal sealed class SetupForm : Form
{
    private readonly TextBox _dir = new();
    private readonly CheckBox _desktop = new();
    private readonly CheckBox _autostart = new();
    private readonly CheckBox _launch = new();
    private readonly Label _status = new();
    private readonly Button _install = new();

    public SetupForm()
    {
        Text = "dsDock 桌面备忘录 — 安装";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 300);
        Font = new Font("Microsoft YaHei UI", 9f);
        BackColor = Color.FromArgb(0x12, 0x18, 0x24);
        ForeColor = Color.FromArgb(0xEE, 0xEE, 0xEE);

        var title = new Label
        {
            Text = "把「桌面备忘录」安装到：",
            Location = new Point(20, 20),
            Size = new Size(520, 24),
            ForeColor = Color.White,
        };
        Controls.Add(title);

        _dir.Text = DefaultTarget();
        _dir.Location = new Point(20, 50);
        _dir.Size = new Size(420, 26);
        _dir.BackColor = Color.FromArgb(0x1C, 0x24, 0x32);
        _dir.ForeColor = Color.White;
        _dir.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(_dir);

        var browse = new Button { Text = "浏览…", Location = new Point(450, 49), Size = new Size(90, 28) };
        browse.Click += (_, _) => Browse();
        Controls.Add(browse);

        _desktop.Text = "创建桌面快捷方式";
        _desktop.Checked = true;
        _desktop.Location = new Point(20, 92);
        _desktop.Size = new Size(240, 24);
        Controls.Add(_desktop);

        _autostart.Text = "开机自动启动";
        _autostart.Location = new Point(20, 120);
        _autostart.Size = new Size(240, 24);
        Controls.Add(_autostart);

        _launch.Text = "安装完成后立即启动";
        _launch.Checked = true;
        _launch.Location = new Point(20, 148);
        _launch.Size = new Size(240, 24);
        Controls.Add(_launch);

        var note = new Label
        {
            Text = "说明：程序数据保存在安装目录的 data\\ 下；重复安装会覆盖程序文件，不会删除你的便利贴数据。",
            Location = new Point(20, 180),
            Size = new Size(520, 40),
            ForeColor = Color.FromArgb(0xAA, 0xBB, 0xCC),
        };
        Controls.Add(note);

        _status.Text = "选择位置后点“开始安装”。";
        _status.Location = new Point(20, 224);
        _status.Size = new Size(520, 24);
        _status.ForeColor = Color.FromArgb(0xBB, 0xE0, 0xFF);
        Controls.Add(_status);

        _install.Text = "开始安装";
        _install.Location = new Point(340, 254);
        _install.Size = new Size(100, 32);
        _install.Click += (_, _) => RunInstall();
        Controls.Add(_install);

        var cancel = new Button { Text = "取消", Location = new Point(448, 254), Size = new Size(90, 32) };
        cancel.Click += (_, _) => Close();
        Controls.Add(cancel);
    }

    private static string DefaultTarget()
    {
        // 默认装在系统盘之外的 D:\dsDock（用户明确表示不希望装到 C 盘）
        DriveInfo[] drives = DriveInfo.GetDrives();
        foreach (DriveInfo drive in drives)
        {
            if (drive.IsReady && drive.Name.StartsWith("D", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(drive.RootDirectory.FullName, "dsDock");
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dsDock");
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择安装位置",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_dir.Text) ? _dir.Text : Path.GetPathRoot(_dir.Text) ?? "D:\\",
            ShowNewFolderButton = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _dir.Text = dialog.SelectedPath;
    }

    private void RunInstall()
    {
        string target = _dir.Text.Trim();
        if (target.Length == 0)
        {
            _status.Text = "请先选择安装位置。";
            return;
        }

        try
        {
            Directory.CreateDirectory(target);
            string existing = Path.Combine(target, "DsDock.exe");
            if (File.Exists(existing))
            {
                if (MessageBox.Show(this, "该目录已安装过，是否覆盖安装？\n（你的便利贴数据不会被删除）", "确认",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                    return;

                // 覆盖安装前先结束正在运行的实例，否则文件被占用
                foreach (Process process in Process.GetProcessesByName("DsDock"))
                {
                    try { process.Kill(); process.WaitForExit(3000); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            _status.Text = "无法使用该目录: " + ex.Message;
            return;
        }

        _install.Enabled = false;
        _status.Text = "正在安装…";
        Application.DoEvents();

        bool ok = Program.Install(target, _desktop.Checked, _autostart.Checked, _launch.Checked,
            message => { _status.Text = message; Application.DoEvents(); });

        _status.Text = ok ? "安装完成。你可以关闭此窗口。" : "安装失败，请查看上面的提示。";
        _install.Enabled = true;
    }
}
