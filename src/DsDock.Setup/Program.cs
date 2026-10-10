using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DsDock.Setup;

/// <summary>
/// dsDock 桌面备忘录 安装程序。
/// 图形界面下让用户选择安装位置；也支持命令行静默安装（供自动化验证）：
///   DsDockSetup.exe --silent --dir "D:\dsDock" [--no-shortcuts] [--no-launch]
/// 载荷以 payload.zip 内嵌在本 exe 中，安装时解包到所选目录。
/// </summary>
internal static class Program
{
    public const string AppName = "桌面备忘录";
    public const string ShortcutName = "DsDock.lnk";

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Contains("--silent"))
        {
            string? dir = Arg(args, "--dir");
            if (string.IsNullOrWhiteSpace(dir))
            {
                Console.Error.WriteLine("--silent 需要 --dir <安装目录>");
                return 2;
            }

            bool ok = Install(dir!, createShortcuts: !args.Contains("--no-shortcuts"), autostart: false,
                              launch: !args.Contains("--no-launch"), log: m => Console.WriteLine(m));
            return ok ? 0 : 1;
        }

        Application.Run(new SetupForm());
        return 0;
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>执行安装：解包载荷 → 快捷方式 → 卸载信息 → 可选启动。</summary>
    public static bool Install(string target, bool createShortcuts, bool autostart, bool launch, Action<string> log)
    {
        try
        {
            target = Path.GetFullPath(target);
            Directory.CreateDirectory(target);
            log($"安装位置: {target}");

            int files = 0;
            using (Stream? payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
            {
                if (payload == null) { log("安装包内部缺少载荷（payload.zip）"); return false; }
                using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    string path = Path.Combine(target, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(path); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    entry.ExtractToFile(path, overwrite: true);
                    files++;
                }
            }
            log($"已释放 {files} 个文件");

            string exe = Path.Combine(target, "DsDock.exe");
            if (!File.Exists(exe)) { log("载荷里没有 DsDock.exe，安装不完整"); return false; }

            if (createShortcuts)
            {
                Shortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName), exe, target);
                string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "dsDock");
                Directory.CreateDirectory(programs);
                Shortcut(Path.Combine(programs, ShortcutName), exe, target);
                log("已创建桌面与开始菜单快捷方式");
            }

            if (autostart)
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)!;
                key.SetValue("DsDock", $"\"{exe}\"");
                log("已添加到开机自启（HKCU Run）");
            }

            WriteUninstall(target, log);

            try
            {
                using RegistryKey uninstall = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\DsDock", writable: true)!;
                uninstall.SetValue("DisplayName", AppName + "（dsDock）");
                uninstall.SetValue("InstallLocation", target);
                uninstall.SetValue("UninstallString", Path.Combine(target, "Uninstall.cmd"));
                uninstall.SetValue("DisplayIcon", exe);
                uninstall.SetValue("NoModify", 1, RegistryValueKind.DWord);
                uninstall.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                // 版本从载荷里的 DsDock.exe 读（安装时才知道装的是哪一版）
                string appVersion = "0.00";
                try
                {
                    FileVersionInfo vi = FileVersionInfo.GetVersionInfo(exe);
                    appVersion = (vi.ProductVersion ?? "").Split('+')[0].Trim();
                    if (appVersion.Length == 0) appVersion = (vi.FileVersion ?? "0.00").Trim();
                }
                catch { /* 读不到就用默认值，不影响安装 */ }
                uninstall.SetValue("DisplayVersion", appVersion);
                log("已在“应用和功能”中登记卸载项");
            }
            catch (Exception ex)
            {
                // 受限环境（或组策略）可能不允许写注册表：这不影响安装本身
                log("未能登记卸载项（可直接运行安装目录里的 Uninstall.cmd）：" + ex.Message);
            }

            if (launch) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = target });
            log("安装完成");
            return true;
        }
        catch (Exception ex)
        {
            log("安装失败: " + ex.GetType().Name + " — " + ex.Message);
            return false;
        }
    }

    /// <summary>生成 Uninstall.cmd（ASCII 内容，避免批处理编码问题）。</summary>
    private static void WriteUninstall(string target, Action<string> log)
    {
        string path = Path.Combine(target, "Uninstall.cmd");
        var lines = new[]
        {
            "@echo off",
            "chcp 65001 >nul 2>&1",
            "echo Removing dsDock ...",
            "reg delete \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\" /v DsDock /f >nul 2>&1",
            "reg delete \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\DsDock\" /f >nul 2>&1",
            "del \"" + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName) + "\" >nul 2>&1",
            "rmdir /s /q \"" + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "dsDock") + "\" >nul 2>&1",
            "del \"" + Path.Combine(target, "DsDock.exe") + "\" >nul 2>&1",
            "for /d %%d in (\"" + target + "\\*\") do rmdir /s /q \"%%d\"",
            "for %%f in (\"" + target + "\\*\") do if /i not \"%%~nxf\"==\"Uninstall.cmd\" del /q \"%%f\"",
            "echo Done. You can delete this folder now: " + target,
            "pause",
        };
        File.WriteAllLines(path, lines);
        log("已生成卸载程序 Uninstall.cmd（同时登记到“应用和功能”）");
    }

    /// <summary>创建 .lnk（用 WScript.Shell，无需额外依赖）。</summary>
    private static void Shortcut(string lnkPath, string exePath, string workingDir)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(lnkPath);
            link.TargetPath = exePath;
            link.WorkingDirectory = workingDir;
            link.IconLocation = exePath + ",0";
            link.Description = AppName;
            link.Save();
        }
        catch (Exception)
        {
            // 快捷方式失败不影响主安装
        }
    }
}
