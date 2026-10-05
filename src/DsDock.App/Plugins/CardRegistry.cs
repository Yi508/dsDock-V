using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DsDock.Card.Abstractions;
using DsDock.Diagnostics;

namespace DsDock.Plugins;

/// <summary>registry.json 里的一条记录（可能校验失败，失败原因保留给卡片库显示灰色占位）。</summary>
internal sealed class CardEntry
{
    public string Id { get; set; } = "";
    public string Folder { get; set; } = "";
    public string ManifestPath { get; set; } = "";
    public CardManifest? Manifest { get; set; }
    public string? Error { get; set; }
    public bool Valid => Manifest != null && Error == null;
}

/// <summary>
/// 读取 registry.json，并逐个校验 Cards/&lt;id&gt;/manifest.json。
/// 规则：只加载"已注册且清单校验通过"的卡片；失败的跳过并记录原因（不抛异常）。
/// </summary>
internal static class CardRegistry
{
    private static string? _deployRoot;

    /// <summary>
    /// 部署根：exe 目录若含 registry.json 就用它；否则逐级向上找（build 输出在 bin/ 下，
    /// 而按项目结构插件与 registry.json 放在仓库根）。找不到则退回 exe 目录。
    /// </summary>
    public static string DeployRoot
    {
        get
        {
            if (_deployRoot != null) return _deployRoot;
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                if (File.Exists(Path.Combine(dir, "registry.json"))) { _deployRoot = dir; return dir; }
                DirectoryInfo? parent = Directory.GetParent(dir.TrimEnd(Path.DirectorySeparatorChar));
                if (parent == null) break;
                dir = parent.FullName;
            }
            _deployRoot = AppContext.BaseDirectory;
            return _deployRoot;
        }
    }

    public static string CardsRoot => Path.Combine(DeployRoot, "Cards");
    public static string RegistryPath => Path.Combine(DeployRoot, "registry.json");

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>读取 registry.json 里的 id 列表（解析失败返回空表）。</summary>
    public static List<string> ReadIds()
    {
        if (!File.Exists(RegistryPath)) return new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(RegistryPath));
            return doc.RootElement.TryGetProperty("cards", out var arr)
                ? arr.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList()
                : new List<string>();
        }
        catch { return new List<string>(); }
    }

    /// <summary>把 id 注册进 registry.json（已存在则跳过）。改前留一份 registry.json.bak 便于回退。</summary>
    public static bool RegisterId(string id, out string detail)
    {
        try
        {
            List<string> ids = ReadIds();
            if (ids.Contains(id)) { detail = $"registry.json 里已注册 {id}（无需修改）"; return true; }

            ids.Add(id);
            if (File.Exists(RegistryPath)) File.Copy(RegistryPath, RegistryPath + ".bak", overwrite: true);
            File.WriteAllText(RegistryPath,
                JsonSerializer.Serialize(new { cards = ids }, new JsonSerializerOptions { WriteIndented = true }));
            detail = $"已注册到 registry.json（共 {ids.Count} 个: {string.Join(", ", ids)}）";
            Log.Info("registry.json 更新: " + detail);
            return true;
        }
        catch (Exception ex)
        {
            detail = "写入 registry.json 失败: " + ex.Message;
            return false;
        }
    }

    /// <summary>从 registry.json 里移除一个 id（本来就没有则视为已处理）。改前留 .bak。</summary>
    public static bool UnregisterId(string id, out string detail)
    {
        try
        {
            List<string> ids = ReadIds();
            if (!ids.Contains(id)) { detail = $"registry.json 里没有 {id}（无需修改）"; return true; }

            ids.Remove(id);
            if (File.Exists(RegistryPath)) File.Copy(RegistryPath, RegistryPath + ".bak", overwrite: true);
            File.WriteAllText(RegistryPath,
                JsonSerializer.Serialize(new { cards = ids }, new JsonSerializerOptions { WriteIndented = true }));
            detail = ids.Count == 0
                ? "已从 registry.json 注销（当前无注册卡片）"
                : $"已从 registry.json 注销（剩余 {ids.Count} 个: {string.Join(", ", ids)}）";
            Log.Info("registry.json 更新: " + detail);
            return true;
        }
        catch (Exception ex)
        {
            detail = "写入 registry.json 失败: " + ex.Message;
            return false;
        }
    }

    public static List<CardEntry> Load(string? overrideRoot = null)
    {
        string root = overrideRoot ?? CardsRoot;
        var result = new List<CardEntry>();

        string registryPath = Path.Combine(overrideRoot ?? DeployRoot, "registry.json");

        if (!File.Exists(registryPath))
        {
            Log.Info($"未找到 registry.json（{registryPath}）：卡片库将为空");
            return result;
        }

        List<string> ids;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(registryPath));
            ids = doc.RootElement.TryGetProperty("cards", out var arr)
                ? arr.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList()
                : new List<string>();
        }
        catch (Exception ex)
        {
            Log.Info($"registry.json 解析失败，卡片库将为空: {ex.Message}");
            return result;
        }

        Log.Info($"registry.json 注册卡片 {ids.Count} 个: {string.Join(", ", ids)}（部署根 {DeployRoot}，插件目录 {root}）");

        foreach (string id in ids)
        {
            var entry = new CardEntry { Id = id, Folder = Path.Combine(root, id) };
            entry.ManifestPath = Path.Combine(entry.Folder, "manifest.json");

            if (!Directory.Exists(entry.Folder))
            {
                entry.Error = $"已注册但缺少目录 Cards/{id}/";
                Log.Info($"卡片跳过: {entry.Error}");
                result.Add(entry);
                continue;
            }
            if (!File.Exists(entry.ManifestPath))
            {
                entry.Error = $"Cards/{id}/ 缺少 manifest.json";
                Log.Info($"卡片跳过: {entry.Error}");
                result.Add(entry);
                continue;
            }

            try
            {
                var manifest = JsonSerializer.Deserialize<CardManifest>(File.ReadAllText(entry.ManifestPath), Json);
                if (manifest == null)
                {
                    entry.Error = "manifest.json 内容为空";
                }
                else if (manifest.Id != id)
                {
                    entry.Error = $"manifest.id({manifest.Id}) 与注册 id({id}) 不一致";
                }
                else
                {
                    entry.Error = manifest.Validate();
                    if (entry.Error == null) entry.Manifest = manifest;
                }
            }
            catch (Exception ex)
            {
                entry.Error = "manifest.json 解析失败: " + ex.Message;
            }

            if (entry.Error != null) Log.Info($"卡片跳过: {id} — {entry.Error}");
            else Log.Info($"卡片清单校验通过: {id} ({entry.Manifest!.Name} v{entry.Manifest.Version}, 允许 {string.Join("/", entry.Manifest.AllowedSizes)})");
            result.Add(entry);
        }

        return result;
    }
}
