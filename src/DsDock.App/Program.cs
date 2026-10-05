using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using DsDock.Appearance;
using DsDock.Diagnostics;
using DsDock.Platform;
using DsDock.Shell;
using DsDock.Storage;

namespace DsDock;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var watch = Stopwatch.StartNew();
        var options = Options.Parse(args);

        if (!SingleInstance.TryAcquire(options.RestartWait)) return 0;

        // 自检绝不能碰用户的 settings.json：自检模式写 settings.selftest.json
        if (options.SelfTestPath != null)
        {
            SettingsStore.FileName = "settings.selftest.json";
            LayoutStore.Suffix = ".selftest";   // 布局/备份同样隔离，自检不碰用户数据
        }

        // 自检会读写外观并落盘：必须跑在独立数据目录，不能覆盖用户自己的 settings.json
        if (options.SelfTestPath != null && string.IsNullOrEmpty(options.ForceDataRoot))
            options.ForceDataRoot = Path.Combine(Path.GetTempPath(), "dsdock-selftest");
        DataRoot.Initialize(options.ForceDataRoot);
        Log.Initialize(DataRoot.LogsDir, $"app-{DateTime.Now:HHmmss}.log");
        Log.Info("=== dsDock 0.1 启动 ===");
        Log.Info("参数: " + options.Summary());
        Log.Info($"数据目录: {DataRoot.Root}（{DataRoot.Source}）— {DataRoot.ProbeDetail}");
        Log.Info($"进程完整性: {ProcessIntegrity.Describe()}；父进程: {ProcessIntegrity.DescribeParent()}");
        Log.Info($"环境: OS {Environment.OSVersion.Version} 64bit={Environment.Is64BitProcess}");

        var settings = SettingsStore.Load();

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Themes/Default.xaml"),
        });

        // 外观：命令行显式指定 > settings.json > 默认
        int alpha = options.AlphaExplicit ? options.Alpha : settings.Alpha;
        int frost = 0;   // 磨砂功能已移除（保留字段仅为兼容旧 settings.json）
        int corner = options.CornerExplicit ? options.Corner : settings.Corner;
        double font = options.FontExplicit ? options.FontSize : settings.FontSize;
        string accent = options.AccentExplicit ? options.Accent : settings.Accent;
        Color accentColor = Theme.TryParseHex(accent, out var parsed) ? parsed : Theme.Accent;
        Theme.Apply(accentColor, font, corner, alpha, frost);
        if (!options.RowsExplicit) options.Rows = settings.Rows;
        if (!options.Locked) options.Locked = settings.Locked;

        var shell = new AppShell(options, app, watch, settings);
        shell.Start();
        app.Run();

        Log.Info($"=== 退出，退出码 {shell.ExitCode} ===");
        return shell.ExitCode;
    }
}
