using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DsDock.Card.Abstractions;
using DsDock.Diagnostics;

namespace DsDock.Plugins;

/// <summary>
/// 卡片安装器：把用户选的文件夹安装成一张卡片。
/// 步骤：找到并校验 manifest → 拷贝到 Cards/&lt;id&gt;/（跳过禁止携带的文件）→ 写入 registry.json → 热加载。
/// 全部失败都返回可读原因，不抛异常（UI 直接把 detail 显示出来）。
/// </summary>
internal static class CardInstaller
{
    /// <summary>绝不能随卡片一起装进来的文件（契约由宿主提供，带进来会造成类型身份冲突）。</summary>
    private static readonly string[] Forbidden =
    {
        "DsDock.Card.Abstractions.dll",
        "DsDock.exe",
        "DsDock.dll",
        "registry.json",
        "settings.json",
        "layout.json",
    };

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>已安装卡片的信息（供自检与 UI 显示）。</summary>
    public sealed record InstallResult(string Id, string Name, string Version, string TargetDir, int FileCount, string RegistryDetail, bool HotLoaded, string Detail);

    /// <summary>
    /// 从 sourceDir 安装一张卡片。成功返回 true 并给出安装结果。
    /// </summary>
    /// <summary>对外入口：任何异常都转成可读原因（绝不让"导入"把应用带崩）。</summary>
    public static bool Install(string sourceDir, out InstallResult? result, out string detail)
    {
        try
        {
            return InstallCore(sourceDir, out result, out detail);
        }
        catch (Exception ex)
        {
            result = null;
            detail = $"安装失败（已阻止崩溃）: {ex.GetType().Name} — {ex.Message}";
            Log.Info(detail);
            return false;
        }
    }

    private static bool InstallCore(string sourceDir, out InstallResult? result, out string detail)
    {
        result = null;

        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
        {
            detail = "文件夹不存在";
            return false;
        }

        // "解压后多套一层目录"的容错：本体目录若没有 manifest.json，就往下一层找一个
        string root = sourceDir;
        if (!File.Exists(Path.Combine(root, "manifest.json")))
        {
            string? nested = Directory.EnumerateDirectories(root)
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "manifest.json")));
            if (nested == null)
            {
                detail = "所选文件夹里没有 manifest.json（也不在下一层子目录里）";
                return false;
            }
            root = nested;
            Log.Info($"导入新卡片: 在子目录找到清单 {root}");
        }

        // 1) 读并校验清单
        CardManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<CardManifest>(File.ReadAllText(Path.Combine(root, "manifest.json")), Json);
        }
        catch (Exception ex)
        {
            detail = "manifest.json 解析失败: " + ex.Message;
            return false;
        }

        if (manifest == null) { detail = "manifest.json 内容为空"; return false; }
        if (string.IsNullOrWhiteSpace(manifest.Id) || manifest.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            detail = $"manifest.id 非法: \"{manifest.Id}\"";
            return false;
        }

        string? invalid = manifest.Validate();
        if (invalid != null) { detail = "清单校验未通过: " + invalid; return false; }

        string entryPath = Path.Combine(root, manifest.Entry!);
        if (!File.Exists(entryPath)) { detail = $"缺少入口文件 {manifest.Entry}"; return false; }

        // 覆盖安装前先卸载同名卡片：否则它的 DLL 被本进程锁着，拷贝必然失败
        if (PreInstallUnload != null)
        {
            try { PreInstallUnload(manifest.Id); }
            catch (Exception ex) { Log.Info("覆盖安装前卸载失败（继续尝试拷贝）: " + ex.Message); }
        }

        // 2) 拷贝到 Cards/<id>/（同名覆盖；跳过禁止携带的文件）
        string target = Path.Combine(CardRegistry.CardsRoot, manifest.Id);
        int copied = 0;
        try
        {
            Directory.CreateDirectory(target);
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (Forbidden.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    Log.Info($"导入新卡片: 跳过禁止携带的文件 {name}（契约由宿主提供）");
                    continue;
                }

                string relative = Path.GetRelativePath(root, file);
                string destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
                copied++;
            }
        }
        catch (Exception ex)
        {
            detail = "拷贝卡片文件失败: " + ex.Message +
                     (ex.Message.Contains("being used by") ? "（该卡片可能仍在运行：已尝试卸载，若仍失败请重启应用后再导入）" : "");
            return false;
        }

        if (copied == 0) { detail = "没有可拷贝的文件"; return false; }
        Log.Info($"导入新卡片: {manifest.Id} → {target}（{copied} 个文件）");

        // 3) 注册进 registry.json
        if (!CardRegistry.RegisterId(manifest.Id, out string registryDetail))
        {
            detail = "文件已拷贝，但" + registryDetail;
            return false;
        }

        // 4) 热加载（不重启）
        bool hotLoaded = false;
        string hotDetail = "未接入热加载";
        if (HotLoader != null)
        {
            try { (hotLoaded, hotDetail) = HotLoader(manifest.Id); }
            catch (Exception ex) { hotLoaded = false; hotDetail = "热加载异常: " + ex.GetType().Name + " — " + ex.Message; Log.Info(hotDetail); }
        }

        result = new InstallResult(manifest.Id, manifest.Name ?? manifest.Id, manifest.Version ?? "", target, copied, registryDetail, hotLoaded, hotDetail);
        detail = $"已安装 {manifest.Id}（{manifest.Name} v{manifest.Version}）→ {target}；{registryDetail}；{(hotLoaded ? "已即时生效，无需重启" : "热加载未生效：" + hotDetail)}";
        Log.Info("导入新卡片完成: " + detail);
        return true;
    }

    /// <summary>
    /// 移除一张已安装的卡片：删除 Cards/&lt;id&gt;/ 目录、从 registry.json 注销、清理它的实例状态文件。
    /// 与 Install 一样全程兜底，任何失败都返回可读原因。
    /// </summary>
    public static bool Uninstall(string id, out string detail)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                detail = $"卡片 id 非法: \"{id}\"";
                return false;
            }

            string dir = Path.Combine(CardRegistry.CardsRoot, id);
            int files = Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count()
                : 0;

            // 删目录带重试：① 程序集回收有延迟，句柄可能晚一点释放；② 从别处拷来的文件可能带只读属性
            for (int i = 0; i < 6 && Directory.Exists(dir); i++)
            {
                try
                {
                    foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                    }
                    Directory.Delete(dir, recursive: true);
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && i < 5)
                {
                    System.Threading.Thread.Sleep(150);
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            }

            bool unregistered = CardRegistry.UnregisterId(id, out string registryDetail);
            int states = StateCleanup?.Invoke(id) ?? 0;

            detail = $"已移除卡片 {id}：删除 {dir}（{files} 个文件）；{registryDetail}" +
                     (states > 0 ? $"；清理 {states} 份卡片状态" : "");
            Log.Info(detail);
            return unregistered;
        }
        catch (Exception ex)
        {
            detail = $"移除失败: {ex.GetType().Name} — {ex.Message}";
            Log.Info(detail);
            return false;
        }
    }

    /// <summary>宿主编织：覆盖安装前卸载同 id 的已加载卡片（释放 DLL 文件句柄）。</summary>
    public static Func<string, bool>? PreInstallUnload { get; set; }

    /// <summary>宿主编织：清理该卡片所有实例的状态文件，返回清理数量。</summary>
    public static Func<string, int>? StateCleanup { get; set; }

    /// <summary>宿主编织：安装完成后立刻把卡片加载进运行时（AppShell 指向 CardRuntime.InstallHot）。</summary>
    public static Func<string, (bool Ok, string Detail)>? HotLoader { get; set; }
}
