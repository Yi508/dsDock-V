using System;
using System.IO;
using System.Text;
using System.Text.Json;
using DsDock.Diagnostics;
using DsDock.Windows;

namespace DsDock.Storage;

/// <summary>
/// settings.json for the whole app (spec: 透明度/磨砂/圆角/字体/强调色/尺寸/位置/锁定/侧边栏全部参数).
/// Written atomically so a crash mid-save cannot leave a half file behind.
/// </summary>
internal sealed class SettingsStore
{
    /// <summary>配置版本：<2 表示还带着旧默认强调色 #3B82F6，加载时迁移成新的 #DB6BBC。</summary>
    public int Version { get; set; } = 2;

    // 外观
    public int Alpha { get; set; } = 90;
    public int Frost { get; set; } = 50;
    public int Corner { get; set; } = 6;
    public double FontSize { get; set; } = 15;
    public string Accent { get; set; } = "#DB6BBC";

    // 主界面
    public int Rows { get; set; } = 3;          // 尺寸挡位 2×1 .. 2×6
    public bool Locked { get; set; }

    /// <summary>全屏应用/游戏运行时自动隐藏侧边栏与主界面（spec）。</summary>
    public bool FullscreenHide { get; set; } = true;

    /// <summary>点击主界面之外（本应用失去焦点）时自动收回侧边栏。默认关。</summary>
    public bool CollapseOnOutsideClick { get; set; } = false;

    /// <summary>容器列数：2（默认）或 4（右下角按钮展开态）。</summary>
    public int ContainerColumns { get; set; } = 2;

    /// <summary>是否显示调试信息（FPS/像素尺寸）。默认关闭，只在需要排查时打开。</summary>
    public bool ShowHud { get; set; }
    public int PanelLeft { get; set; } = -1;
    public int PanelTop { get; set; } = -1;

    // 侧边栏
    public string SidebarEdge { get; set; } = "Right";
    public double SidebarWidth { get; set; } = 8;
    public int SidebarAlpha { get; set; } = 50;
    public double SidebarOffset { get; set; } = 0.5;   // 沿边位置（0..1，相对工作区）
    public bool SidebarVisible { get; set; } = true;
    public string SidebarMonitor { get; set; } = "";

    // 辅助窗口位置
    public int SettingsLeft { get; set; } = -1;
    public int SettingsTop { get; set; } = -1;

    /// <summary>
    /// 配置文件名。自检模式会切换成 settings.selftest.json —— 自检里包含"改外观→落盘→读回"
    /// 的断言，用同一个文件就会把用户自己调好的外观覆盖掉。
    /// </summary>
    public static string FileName { get; set; } = "settings.json";

    public static string FilePath => Path.Combine(DataRoot.Root, FileName);

    public static SettingsStore Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<SettingsStore>(File.ReadAllText(FilePath));
                if (loaded != null)
                {
                    if (loaded.Version < 2)
                    {
                        if (string.Equals(loaded.Accent, "#3B82F6", StringComparison.OrdinalIgnoreCase))
                        {
                            loaded.Accent = "#DB6BBC";
                            Log.Info("settings.json 迁移：默认强调色 #3B82F6 → #DB6BBC");
                        }
                        loaded.Version = 2;
                    }
                    Log.Info($"读取 settings.json: alpha={loaded.Alpha} frost={loaded.Frost} corner={loaded.Corner} " +
                             $"font={loaded.FontSize} accent={loaded.Accent} rows={loaded.Rows} locked={loaded.Locked} " +
                             $"edge={loaded.SidebarEdge} sidebar={loaded.SidebarWidth}px/{loaded.SidebarAlpha}%");
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Info("settings.json 损坏或不可读，使用默认值: " + ex.Message);
        }
        return new SettingsStore();
    }

    public void Save()
    {
        try
        {
            AtomicJson.Write(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Info("写入 settings.json 失败: " + ex.Message);
        }
    }

    public DockEdge Edge => DockEdges.Parse(SidebarEdge);
}

/// <summary>Write JSON via a temp file + replace, so readers never see a partial file.</summary>
internal static class AtomicJson
{
    public static void Write(string path, string content)
    {
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        string temp = Path.Combine(dir, Path.GetFileName(path) + ".tmp");
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        if (File.Exists(path))
        {
            try
            {
                File.Replace(temp, path, null);
            }
            catch (Exception ex)
            {
                Log.Info("File.Replace 失败，退化为覆盖写: " + ex.Message);
                File.Copy(temp, path, true);
                File.Delete(temp);
            }
        }
        else File.Move(temp, path);
    }
}
