using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DsDock.Diagnostics;

namespace DsDock.Storage;

/// <summary>容器里一张卡片实例的布局记录（layout.json）。</summary>
internal sealed class CardInstanceRecord
{
    public string InstanceId { get; set; } = "";
    public string CardId { get; set; } = "";
    public int Col { get; set; }
    public int Row { get; set; }
    public int Columns { get; set; } = 1;
    public int Rows { get; set; } = 1;
}

internal sealed class LayoutFile
{
    public int Version { get; set; } = 1;
    public int PanelRows { get; set; } = 3;
    public List<CardInstanceRecord> Cards { get; set; } = new();
}

/// <summary>
/// 数据落盘：layout.json（容器布局）+ data/cards/&lt;instanceId&gt;.json（每张卡片自己的状态）。
/// 全部原子写；读取失败不抛异常，而是返回"损坏"信息交给宿主回退 + 提示用户。
/// </summary>
internal static class LayoutStore
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>文件名后缀：自检模式设为 ".selftest"，绝不覆盖用户数据（与 settings 同样的隔离思路）。</summary>
    public static string Suffix { get; set; } = "";

    public static string LayoutPath => Path.Combine(DataRoot.Root, "layout" + Suffix + ".json");
    public static string StateDir => Path.Combine(DataRoot.Root, "cards" + Suffix);

    public static LayoutFile Load(out string detail)
    {
        if (!File.Exists(LayoutPath))
        {
            detail = "无 layout.json（首次运行）";
            return new LayoutFile();
        }

        try
        {
            LayoutFile? layout = JsonSerializer.Deserialize<LayoutFile>(File.ReadAllText(LayoutPath));
            if (layout == null)
            {
                detail = "损坏: layout.json 内容为空";
                return new LayoutFile();
            }
            detail = $"读取 layout.json: 挡位 2×{layout.PanelRows}，卡片 {layout.Cards.Count} 张";
            return layout;
        }
        catch (Exception ex)
        {
            detail = "损坏: layout.json 解析失败 — " + ex.Message;
            return new LayoutFile();
        }
    }

    public static void Save(LayoutFile layout)
    {
        try
        {
            AtomicJson.Write(LayoutPath, JsonSerializer.Serialize(layout, Indented));
        }
        catch (Exception ex)
        {
            Log.Info("写入 layout.json 失败: " + ex.Message);
        }
    }

    public static void SaveState(string instanceId, string json)
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            AtomicJson.Write(Path.Combine(StateDir, instanceId + ".json"), json);
        }
        catch (Exception ex)
        {
            Log.Info($"写入卡片状态 {instanceId} 失败: {ex.Message}");
        }
    }

    public static string LoadState(string instanceId)
    {
        try
        {
            string path = Path.Combine(StateDir, instanceId + ".json");
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch (Exception ex)
        {
            Log.Info($"读取卡片状态 {instanceId} 失败: {ex.Message}");
            return "";
        }
    }

    /// <summary>移除卡片时**不**删状态文件（spec：数据保留），只有真正删除便利贴时才调用。</summary>
    public static void DeleteState(string instanceId)
    {
        try
        {
            string path = Path.Combine(StateDir, instanceId + ".json");
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Info($"删除卡片状态 {instanceId} 失败: {ex.Message}");
        }
    }
}

/// <summary>整包快照：settings + layout + 每张卡片状态原样保存，便于整包回退。</summary>
internal sealed class BackupSnapshot
{
    public int Version { get; set; } = 1;
    public string SavedAt { get; set; } = "";
    public string Settings { get; set; } = "";
    public string Layout { get; set; } = "";
    public Dictionary<string, string> States { get; set; } = new();
}

/// <summary>
/// 备份轮转 backup_1..3.json（1 最新）+ 单日一次。损坏时从最新的备份整包回退。
/// </summary>
internal static class BackupService
{
    private const int Keep = 3;
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    public static string BackupPath(int index) => Path.Combine(DataRoot.Root, $"backup_{index}{LayoutStore.Suffix}.json");
    private static string StampPath => Path.Combine(DataRoot.Root, "backup_last" + LayoutStore.Suffix + ".txt");

    public static string LastBackupDate
    {
        get
        {
            try { return File.Exists(StampPath) ? File.ReadAllText(StampPath).Trim() : ""; }
            catch { return ""; }
        }
    }

    /// <summary>整包快照并轮转。返回可读结果（写进日志/自检）。</summary>
    public static string Rotate(string reason)
    {
        try
        {
            Directory.CreateDirectory(DataRoot.Root);
            var snapshot = new BackupSnapshot { SavedAt = DateTime.Now.ToString("O") };

            if (File.Exists(SettingsStore.FilePath)) snapshot.Settings = File.ReadAllText(SettingsStore.FilePath);
            if (File.Exists(LayoutStore.LayoutPath)) snapshot.Layout = File.ReadAllText(LayoutStore.LayoutPath);
            if (Directory.Exists(LayoutStore.StateDir))
            {
                foreach (string file in Directory.GetFiles(LayoutStore.StateDir, "*.json"))
                    snapshot.States[Path.GetFileNameWithoutExtension(file)] = File.ReadAllText(file);
            }

            for (int i = Keep; i > 1; i--)
            {
                if (File.Exists(BackupPath(i - 1))) File.Copy(BackupPath(i - 1), BackupPath(i), true);
            }
            AtomicJson.Write(BackupPath(1), JsonSerializer.Serialize(snapshot, Compact));
            File.WriteAllText(StampPath, DateTime.Now.ToString("yyyy-MM-dd"));

            string detail = $"备份完成（{reason}）: backup_1.json，卡片状态 {snapshot.States.Count} 份，布局 {snapshot.Layout.Length} 字节";
            Log.Info(detail);
            return detail;
        }
        catch (Exception ex)
        {
            string detail = "备份失败: " + ex.Message;
            Log.Info(detail);
            return detail;
        }
    }

    /// <summary>今天是首次运行/换日才备份（"启动 + 每日"）。</summary>
    public static string RotateIfNeeded()
    {
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        return LastBackupDate == today ? "今日已备份，跳过" : Rotate("启动/每日");
    }

    /// <summary>从最新的可用备份整包回退；返回是否成功。</summary>
    public static bool TryRestoreLatest(out string detail)
    {
        for (int i = 1; i <= Keep; i++)
        {
            string path = BackupPath(i);
            if (!File.Exists(path)) continue;
            try
            {
                BackupSnapshot? snapshot = JsonSerializer.Deserialize<BackupSnapshot>(File.ReadAllText(path));
                if (snapshot == null) continue;

                if (snapshot.Settings.Length > 0) AtomicJson.Write(SettingsStore.FilePath, snapshot.Settings);
                if (snapshot.Layout.Length > 0) AtomicJson.Write(LayoutStore.LayoutPath, snapshot.Layout);
                Directory.CreateDirectory(LayoutStore.StateDir);
                foreach (var pair in snapshot.States)
                    AtomicJson.Write(Path.Combine(LayoutStore.StateDir, pair.Key + ".json"), pair.Value);

                detail = $"已从 backup_{i}.json 回退（{snapshot.SavedAt}，卡片状态 {snapshot.States.Count} 份）";
                Log.Info(detail);
                return true;
            }
            catch (Exception ex)
            {
                Log.Info($"backup_{i}.json 不可用: {ex.Message}");
            }
        }

        detail = "没有可用备份，已按空数据启动";
        return false;
    }
}
