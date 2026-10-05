using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DsDock.Diagnostics;

namespace DsDock.Storage;

/// <summary>单文件数据包：settings + layout + 全部卡片状态。导出的就是这个。</summary>
internal sealed class DataPackage
{
    public int Version { get; set; } = 1;
    public string ExportedAt { get; set; } = "";
    public string Settings { get; set; } = "";
    public string Layout { get; set; } = "";
    public Dictionary<string, string> States { get; set; } = new();

    /// <summary>开机自启（HKCU Run）。它是系统状态而非 settings.json，所以要单独打包才可迁移。</summary>
    public bool Startup { get; set; }
}

/// <summary>
/// 导入 / 导出 / 重置。
/// 导出由用户选择保存位置；导入同样由用户选择文件，且**先做格式校验**再回填；
/// 重置前自动整包备份，保证可回退。
/// </summary>
internal static class ImportExport
{
    /// <summary>文件对话框的过滤字符串：默认只显示本应用的数据包。</summary>
    public const string PackageFilter =
        "dsDock 数据包 (*.dsdock.json)|*.dsdock.json|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>宿主编织：读取/写入"开机自启"状态（打包进导出、导入时恢复）。</summary>
    public static Func<bool>? StartupStateProvider { get; set; }
    public static bool LastImportedStartup { get; private set; }

    public static string ExportDir => Path.Combine(DataRoot.Root, "export");

    /// <summary>导出到指定路径（用户在保存对话框里选的位置）。</summary>
    public static bool ExportTo(string path, out string detail)
    {
        try
        {
            var package = new DataPackage { ExportedAt = DateTime.Now.ToString("O") };
            package.Startup = StartupStateProvider?.Invoke() ?? false;   // 由宿主提供（避免 Storage 依赖 Shell）

            if (File.Exists(SettingsStore.FilePath)) package.Settings = File.ReadAllText(SettingsStore.FilePath);
            if (File.Exists(LayoutStore.LayoutPath)) package.Layout = File.ReadAllText(LayoutStore.LayoutPath);
            if (Directory.Exists(LayoutStore.StateDir))
            {
                foreach (string file in Directory.GetFiles(LayoutStore.StateDir, "*.json"))
                    package.States[Path.GetFileNameWithoutExtension(file)] = File.ReadAllText(file);
            }

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            AtomicJson.Write(path, JsonSerializer.Serialize(package, Indented));

            detail = $"已导出 {Path.GetFileName(path)}（布局 {package.Layout.Length} 字节，卡片状态 {package.States.Count} 份）";
            Log.Info(detail + $" → {path}");
            return true;
        }
        catch (Exception ex)
        {
            detail = "导出失败: " + ex.Message;
            Log.Info(detail);
            return false;
        }
    }

    /// <summary>
    /// 导入前的格式校验：必须是 .json、能解析成数据包、且布局/设置/卡片状态至少有一段有内容；
    /// 布局段还必须是合法 JSON。不合格时给出可读原因（这就是"文件格式检验"）。
    /// </summary>
    public static bool Validate(string path, out DataPackage? package, out string reason)
    {
        package = null;
        try
        {
            if (!File.Exists(path)) { reason = "文件不存在"; return false; }
            if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            {
                reason = "不是 JSON 数据包（扩展名应为 .json）";
                return false;
            }

            string text = File.ReadAllText(path);
            if (text.Trim().Length < 2) { reason = "文件为空"; return false; }

            DataPackage? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<DataPackage>(text);
            }
            catch (Exception ex)
            {
                reason = "不是有效的 JSON: " + ex.Message;
                return false;
            }

            if (parsed == null) { reason = "内容不是 dsDock 数据包"; return false; }
            if (parsed.Layout.Length == 0 && parsed.Settings.Length == 0 && parsed.States.Count == 0)
            {
                reason = "数据包里没有任何内容（布局/设置/卡片状态都为空）";
                return false;
            }

            if (parsed.Layout.Length > 0)
            {
                try
                {
                    if (JsonSerializer.Deserialize<LayoutFile>(parsed.Layout) == null)
                    {
                        reason = "数据包的布局段无法解析";
                        return false;
                    }
                }
                catch
                {
                    reason = "数据包的布局段不是合法 JSON";
                    return false;
                }
            }

            package = parsed;
            reason = $"格式校验通过（导出于 {parsed.ExportedAt}，布局 {parsed.Layout.Length} 字节，卡片状态 {parsed.States.Count} 份）";
            return true;
        }
        catch (Exception ex)
        {
            reason = "格式校验失败: " + ex.Message;
            return false;
        }
    }

    /// <summary>导出到默认目录（脚本 / 自检用）；返回文件路径，失败返回空串。</summary>
    public static string Export(out bool ok)
    {
        try
        {
            Directory.CreateDirectory(ExportDir);
            string path = Path.Combine(ExportDir, $"dsDock-{DateTime.Now:yyyyMMdd-HHmmss}{LayoutStore.Suffix}.json");
            ok = ExportTo(path, out _);
            return ok ? path : "";
        }
        catch (Exception ex)
        {
            ok = false;
            Log.Info("导出失败: " + ex.Message);
            return "";
        }
    }

    /// <summary>从选定文件导入（先格式校验，再整包备份，再回填）。</summary>
    public static bool Import(string path, out string detail)
    {
        try
        {
            if (!Validate(path, out DataPackage? package, out string reason))
                return Fail(reason, out detail);
            if (package == null) return Fail("数据包为空", out detail);

            BackupService.Rotate("导入前");

            if (package.Settings.Length > 0) AtomicJson.Write(SettingsStore.FilePath, package.Settings);
            if (package.Layout.Length > 0) AtomicJson.Write(LayoutStore.LayoutPath, package.Layout);

            Directory.CreateDirectory(LayoutStore.StateDir);
            foreach (KeyValuePair<string, string> pair in package.States)
                AtomicJson.Write(Path.Combine(LayoutStore.StateDir, pair.Key + ".json"), pair.Value);

            LastImportedStartup = package.Startup;
            detail = $"已导入 {Path.GetFileName(path)}（导出于 {package.ExportedAt}，卡片状态 {package.States.Count} 份，开机自启={package.Startup}）";
            Log.Info(detail + "；重启后生效");
            return true;
        }
        catch (Exception ex)
        {
            return Fail("导入失败: " + ex.Message, out detail);
        }
    }

    /// <summary>重置：先整包备份，再删掉 settings / layout / cards（保留导出与备份）。</summary>
    public static bool ResetAll(out string detail)
    {
        try
        {
            string backup = BackupService.Rotate("重置前");

            if (File.Exists(SettingsStore.FilePath)) File.Delete(SettingsStore.FilePath);
            if (File.Exists(LayoutStore.LayoutPath)) File.Delete(LayoutStore.LayoutPath);
            if (Directory.Exists(LayoutStore.StateDir)) Directory.Delete(LayoutStore.StateDir, true);

            detail = $"所有数据已清空（{backup}）";
            Log.Info(detail);
            return true;
        }
        catch (Exception ex)
        {
            detail = "重置失败: " + ex.Message;
            Log.Info(detail);
            return false;
        }
    }

    private static bool Fail(string reason, out string detail)
    {
        detail = reason;
        Log.Info("导入: " + reason);
        return false;
    }
}
