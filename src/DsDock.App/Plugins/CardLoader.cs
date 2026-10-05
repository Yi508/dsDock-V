using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using DsDock.Card.Abstractions;
using DsDock.Diagnostics;

namespace DsDock.Plugins;

/// <summary>
/// 每个卡片一个 AssemblyLoadContext 做隔离加载。
/// 关键点：契约程序集 DsDock.Card.Abstractions 必须解析回"宿主已加载的那一份"，
/// 否则插件里的 ICardFactory 与宿主里的不是同一个类型，转型会抛 InvalidCastException。
/// </summary>
internal sealed class CardLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private static readonly Assembly HostContract = typeof(ICardFactory).Assembly;
    private static readonly Assembly HostSelf = typeof(CardLoadContext).Assembly;

    // isCollectible 必须为 true：否则 Unload() 无效，卡片 DLL 的文件句柄会一直握到进程退出，
    // 表现为"移除卡片后再导入，拷贝报 being used by another process"。
    public CardLoadContext(string cardDllPath) : base($"card:{Path.GetFileNameWithoutExtension(cardDllPath)}", isCollectible: true)
        => _resolver = new AssemblyDependencyResolver(cardDllPath);

    protected override Assembly? Load(AssemblyName name)
    {
        // 契约与宿主程序集一律回宿主（这是隔离加载最容易踩的坑）
        if (name.Name == HostContract.GetName().Name) return HostContract;
        if (name.Name == HostSelf.GetName().Name) return HostSelf;

        string? path = _resolver.ResolveAssemblyToPath(name);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}

/// <summary>一个卡片类型的加载结果（失败时保留原因，供卡片库显示灰色占位 + 系统气泡）。</summary>
internal sealed class LoadedCard
{
    public CardEntry Entry { get; }
    public ICardFactory? Factory { get; private set; }
    public string? Error { get; private set; }
    public string AssemblyPath { get; }
    private CardLoadContext? _context;
    public string ContractIdentity { get; private set; } = "";
    public bool Ok => Factory != null && Error == null;

    public LoadedCard(CardEntry entry)
    {
        Entry = entry;
        AssemblyPath = entry.Valid
            ? Path.Combine(entry.Folder, entry.Manifest!.Entry)
            : "";
    }

    public bool Load()
    {
        if (!Entry.Valid)
        {
            Error = Entry.Error ?? "清单未通过校验";
            return false;
        }
        if (!File.Exists(AssemblyPath))
        {
            Error = $"缺少入口程序集 {Path.GetFileName(AssemblyPath)}";
            return false;
        }

        try
        {
            // 影子拷贝：Cards\ 下的原始 DLL 绝不映射，移除/覆盖安装都不会再遇到"文件被占用"
            string loadPath = ShadowCopy();
            var context = new CardLoadContext(loadPath);
            _context = context;
            Assembly assembly = context.LoadFromAssemblyPath(loadPath);

            Type? factoryType = assembly.GetTypes()
                .FirstOrDefault(t => typeof(ICardFactory).IsAssignableFrom(t) && !t.IsAbstract && t.IsPublic);
            if (factoryType == null)
            {
                Error = "程序集里没有公开的 ICardFactory 实现";
                Unload();   // 失败也要卸载：否则 DLL 被锁，移除/覆盖都会失败
                return false;
            }

            object? instance = Activator.CreateInstance(factoryType);
            if (instance is not ICardFactory factory)
            {
                // 走到这里基本就是契约程序集被加载了两份
                Error = "ICardFactory 类型不匹配（契约程序集被重复加载）";
                Unload();
                return false;
            }
            if (factory.Id != Entry.Id)
            {
                Error = $"factory.Id({factory.Id}) 与注册 id({Entry.Id}) 不一致";
                Unload();
                return false;
            }

            Factory = factory;
            ContractIdentity = $"ICardFactory 来自 {factoryType.Assembly.GetName().Name}，" +
                               $"契约程序集 = {typeof(ICardFactory).Assembly.GetName().Name} " +
                               $"(同一份={ReferenceEquals(factoryType.Assembly, typeof(ICardFactory).Assembly) == false})";
            Log.Info($"卡片加载成功: {Entry.Id} → {factoryType.FullName} @ {Path.GetFileName(AssemblyPath)}");
            return true;
        }
        catch (Exception ex)
        {
            Error = "加载异常: " + ex.Message;
            Log.Info($"卡片加载失败: {Entry.Id} — {Error}");
            Unload();   // 半途失败也要卸载，避免文件句柄泄漏
            return false;
        }
    }

    /// <summary>
    /// 影子拷贝：把整个卡片目录复制到临时目录，再从那里加载程序集。
    /// 关键收益：Cards\&lt;id&gt;\ 下的原始 DLL **从不被映射** →
    ///   ① 移除卡片时目录一定能删掉；
    ///   ② 覆盖导入同一张卡片时不会遇到 being used by another process；
    ///   ③ 运行中的实例继续用旧版本，直到重启（新版本下次启动生效）。
    /// </summary>
    private string ShadowCopy()
    {
        try
        {
            string shadowRoot = Path.Combine(Path.GetTempPath(), "DsDock", "cards", Entry.Id, FolderStamp());
            Directory.CreateDirectory(shadowRoot);
            foreach (string file in Directory.EnumerateFiles(Entry.Folder, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(Entry.Folder, file);
                string destination = Path.Combine(shadowRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
            }
            string shadowEntry = Path.Combine(shadowRoot, Path.GetFileName(AssemblyPath));
            Log.Info($"卡片影子拷贝: {Entry.Id} → {shadowEntry}");
            return shadowEntry;
        }
        catch (Exception ex)
        {
            Log.Info($"影子拷贝失败（改为直接加载原文件）: {ex.Message}");
            return AssemblyPath;
        }
    }

    /// <summary>卡片目录内容戳（文件名+大小+修改时间），用来区分版本、避免复用旧影子。</summary>
    private string FolderStamp()
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            foreach (string file in Directory.EnumerateFiles(Entry.Folder, "*", SearchOption.AllDirectories).OrderBy(f => f))
            {
                var info = new FileInfo(file);
                sb.Append(Path.GetFileName(file)).Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks).Append(';');
            }
            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
        }
        catch { return DateTime.UtcNow.Ticks.ToString("x"); }
    }

    /// <summary>
    /// 卸载：释放卡片 DLL 的文件句柄。移除卡片 / 覆盖安装前必须调用，
    /// 否则目标文件被本进程锁住（拷贝会报 being used by another process）。
    /// Unload() 只是"标记可回收"，真正释放句柄要等 GC 回收。
    /// </summary>
    public void Unload()
    {
        Factory = null;
        CardLoadContext? context = _context;
        _context = null;
        if (context == null) return;

        try { context.Unload(); }
        catch (Exception ex) { Log.Info($"卸载卡片程序集失败: {ex.Message}"); }

        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Log.Info($"卡片已卸载程序集: {Entry.Id}（文件句柄应已释放）");
    }
}
