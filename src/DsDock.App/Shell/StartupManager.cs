using System;
using DsDock.Diagnostics;
using Microsoft.Win32;

namespace DsDock.Shell;

/// <summary>
/// 开机自启：写/删 HKCU 的 Run 键（当前用户级，不需要管理员权限）。
/// 关掉时只删自己那一项，不动系统的其它项。
/// </summary>
internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DsDock";

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(ValueName) != null;
        }
        catch (Exception ex)
        {
            Log.Info("读取开机自启状态失败: " + ex.Message);
            return false;
        }
    }

    public static bool Toggle() => IsEnabled() ? Disable() : Enable();

    public static bool Enable()
    {
        try
        {
            string exe = Environment.ProcessPath ?? "";
            if (exe.Length == 0) return false;
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
            key.SetValue(ValueName, $"\"{exe}\"");
            Log.Info($"开机自启已开启: {exe}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Info("开启开机自启失败（权限不足？）: " + ex.Message);
            return false;
        }
    }

    public static bool Disable()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            key?.DeleteValue(ValueName, false);
            Log.Info("开机自启已关闭");
            return true;
        }
        catch (Exception ex)
        {
            Log.Info("关闭开机自启失败: " + ex.Message);
            return false;
        }
    }
}
