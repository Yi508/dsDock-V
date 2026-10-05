using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DsDock.Card.Abstractions;
using DsDock.Diagnostics;

namespace DsDock.Plugins;

/// <summary>
/// 卡片运行时：读注册表 → 每个卡片一个 AssemblyLoadContext 隔离加载 → 创建实例 →
/// 给卡片提供宿主能力（ICardContext）→ 维护实例清单与状态。
/// </summary>
internal sealed class CardRuntime
{
    private readonly List<LoadedCard> _cards = new();
    private readonly Dictionary<string, List<string>> _instances = new();
    private readonly Dictionary<string, string> _state = new();
    private readonly Dictionary<string, CardSize> _sizes = new();
    private readonly List<Context> _contexts = new();
    private readonly Dictionary<string, ICard> _byInstance = new();

    public IReadOnlyList<LoadedCard> Cards => _cards;
    public string RegistryDetail { get; private set; } = "(未加载)";
    public int LoadedCount => _cards.Count(c => c.Ok);
    public int FailedCount => _cards.Count(c => !c.Ok);

    /// <summary>卡片请求移除自己（宿主把容器里的视图摘掉，数据保留）。</summary>
    /// <summary>卡片加载失败通知（title, text）→ 宿主弹系统气泡。</summary>
    /// <summary>卡片请求弹出窗口（宿主实现：置顶弹窗 + 装饰）。</summary>
    public Func<string, FrameworkElement, IEditorPopup>? PopupRequested { get; set; }

    /// <summary>卡片请求系统通知（title, text）→ 宿主发送托盘气泡。</summary>
    public Func<string, string, bool>? NotifyRequested { get; set; }

    public Action<string, string>? LoadFailureNotified { get; set; }

    public Action<string>? RemoveRequested { get; set; }

    /// <summary>卡片请求改尺寸（宿主负责布局约束）。</summary>
    public Action<string, CardSize>? ResizeRequested { get; set; }

    /// <summary>外观变化时通知所有卡片实例。</summary>
    public event Action<Color, double>? ThemeChanged;

    public void Load()
    {
        var entries = CardRegistry.Load();
        foreach (CardEntry entry in entries)
        {
            var card = new LoadedCard(entry);
            card.Load();
            _cards.Add(card);
        }
        RegistryDetail = $"注册 {_cards.Count} 个，加载成功 {LoadedCount} 个，失败 {FailedCount} 个";
        Log.Info("卡片运行时: " + RegistryDetail + "；" + string.Join(" | ", _cards.Select(Describe)));

        // spec：卡片加载失败要记录日志 + 通过 Windows 系统通知提示用户，其余卡片照常
        foreach (LoadedCard failed in _cards.Where(c => !c.Ok))
            LoadFailureNotified?.Invoke("卡片加载失败", $"{failed.Entry.Id}：{failed.Error}（已跳过，其他卡片照常）");
    }

    private static string Describe(LoadedCard c)
        => c.Ok ? $"{c.Entry.Id}=OK" : $"{c.Entry.Id}=失败({c.Error})";

    public LoadedCard? Find(string id) => _cards.FirstOrDefault(c => c.Entry.Id == id);

    /// <summary>
    /// 热加载一张刚安装的卡片（不重启应用）：重新读 registry.json，为这张卡片单独校验+隔离加载。
    /// 同名且已加载的卡片：文件已更新但不动正在运行的实例（返回 false 并说明需重启）。
    /// </summary>
    public bool InstallHot(string id, out string detail)
    {
        try
        {
            return InstallHotCore(id, out detail);
        }
        catch (Exception ex)
        {
            detail = $"热加载异常（已阻止崩溃）: {ex.GetType().Name} — {ex.Message}";
            Log.Info(detail);
            return false;
        }
    }

    private bool InstallHotCore(string id, out string detail)
    {
        LoadedCard? existing = Find(id);
        if (existing is { Ok: true })
        {
            detail = $"卡片 {id} 已在运行，文件已更新（重启后生效）";
            return false;
        }
        if (existing != null) _cards.Remove(existing);   // 之前加载失败的，允许重试

        CardEntry? entry = CardRegistry.Load().FirstOrDefault(e => e.Id == id);
        if (entry == null) { detail = $"registry.json 里找不到 id={id}"; return false; }
        if (entry.Error != null) { detail = entry.Error; return false; }

        var card = new LoadedCard(entry);
        card.Load();
        _cards.Add(card);
        RegistryDetail = $"注册 {_cards.Count} 个，加载成功 {LoadedCount} 个，失败 {FailedCount} 个";
        detail = card.Ok ? "" : (card.Error ?? "加载失败");
        Log.Info($"卡片热加载: {id} → {(card.Ok ? "成功（即时可用）" : "失败：" + detail)}");
        if (!card.Ok) LoadFailureNotified?.Invoke("卡片加载失败", $"{id}：{detail}");
        return card.Ok;
    }

    /// <summary>卸载一张卡片的运行时账目（移除卡片后调用；不重启也立即从卡片库消失）。</summary>
    public bool UnloadCard(string id)
    {
        LoadedCard? card = Find(id);
        if (card != null)
        {
            card.Unload();          // 释放 DLL 文件句柄（否则后续删除/覆盖都会失败）
            _cards.Remove(card);
        }
        _instances.Remove(id);
        RegistryDetail = $"注册 {_cards.Count} 个，加载成功 {LoadedCount} 个，失败 {FailedCount} 个";
        Log.Info($"卡片已卸载: {id}（剩余 {_cards.Count} 个）");
        return card != null;
    }

    /// <summary>某卡片当前在容器里的实例 id 列表（用于移除卡片时清理状态文件）。</summary>
    public IReadOnlyList<string> InstancesOf(string id)
        => _instances.TryGetValue(id, out var list) ? list.ToList() : Array.Empty<string>();

    /// <summary>自检用：从运行时清单里移除一张卡片条目（不动文件）。</summary>
    public void RemoveEntryForTest(string id)
    {
        LoadedCard? card = Find(id);
        if (card != null) _cards.Remove(card);
        RegistryDetail = $"注册 {_cards.Count} 个，加载成功 {LoadedCount} 个，失败 {FailedCount} 个";
    }

    public int CountOf(string id) => _instances.TryGetValue(id, out var list) ? list.Count : 0;

    public int MaxInstancesOf(string id) => Find(id)?.Entry.Manifest?.MaxInstances ?? 0;

    public bool CanAdd(string id, out string reason)
    {
        LoadedCard? card = Find(id);
        if (card == null) { reason = $"未知卡片 {id}"; return false; }
        if (!card.Ok) { reason = $"卡片 {id} 加载失败：{card.Error}"; return false; }
        int max = card.Entry.Manifest!.MaxInstances;
        if (CountOf(id) >= max) { reason = $"卡片 {id} 已达上限 maxInstances={max}"; return false; }
        reason = "";
        return true;
    }

    public ICard? Create(string id, string instanceId, CardSize size, out string error)
    {
        LoadedCard? card = Find(id);
        if (card?.Factory == null) { error = $"卡片 {id} 未加载"; return null; }

        try
        {
            ICard instance = card.Factory.Create();
            if (!_instances.TryGetValue(id, out var list)) { list = new List<string>(); _instances[id] = list; }
            list.Add(instanceId);
            _sizes[instanceId] = size;
            _byInstance[instanceId] = instance;

            var context = new Context(this, instanceId, card, size);
            _contexts.Add(context);
            instance.OnAttached(context);
            error = "";
            return instance;
        }
        catch (Exception ex)
        {
            error = $"创建卡片 {id} 失败: {ex.Message}";
            Log.Info(error);
            return null;
        }
    }

    public void UpdateSize(string instanceId, CardSize size)
    {
        _sizes[instanceId] = size;
        foreach (Context context in _contexts.Where(c => c.InstanceId == instanceId))
            context.RefreshSize(size);

        // 卡片自己也要知道尺寸变了，否则它仍按旧尺寸排版
        if (_byInstance.TryGetValue(instanceId, out ICard? card))
        {
            try { card.OnSizeChanged(size); }
            catch (Exception ex) { Log.Info($"卡片 {instanceId} OnSizeChanged 异常: {ex.Message}"); }
        }
    }

    /// <summary>
    /// 自检用：把一张已挂载的卡片"摘下来再挂回去"，走真实的 OnDetached → OnAttached → LoadState 路径
    /// （用来验证"启动时发现提醒已过期要立即触发"这类逻辑）。
    /// </summary>
    /// <summary>自检用：丢弃内存里的状态缓存，让下一次 LoadState 真的从磁盘读。</summary>
    public void ForgetStateForTest(string instanceId) => _state.Remove(instanceId);

    public bool ReattachForTest(string instanceId)
    {
        Context? context = _contexts.FirstOrDefault(c => c.InstanceId == instanceId);
        if (context == null || !_byInstance.TryGetValue(instanceId, out ICard? card)) return false;
        card.OnDetached();
        card.OnAttached(context);
        Log.Info($"自检：卡片 {instanceId} 已重新挂载");
        return true;
    }

    public void Remove(string instanceId)
    {
        _contexts.RemoveAll(c => c.InstanceId == instanceId);
        foreach (var pair in _instances) pair.Value.Remove(instanceId);
        _sizes.Remove(instanceId);
        _byInstance.Remove(instanceId);
        // 状态刻意保留在内存（spec：移除后数据保留；M5 落盘到 data/cards/<id>.json）
    }

    public void ApplyTheme(Color accent, double fontSize) => ThemeChanged?.Invoke(accent, fontSize);

    public string StateOf(string instanceId) => _state.TryGetValue(instanceId, out string? json) ? json : "";

    /// <summary>宿主没有提供弹窗能力时的兜底（不会崩，只是界面不出现）。</summary>
    private sealed class NullPopup : IEditorPopup
    {
        public void Close() { }
        public bool IsOpen => false;
    }

    /// <summary>宿主提供给卡片的全部能力。</summary>
    private sealed class Context : ICardContext
    {
        private readonly CardRuntime _runtime;
        private readonly LoadedCard _card;
        private CardSize _size;

        public Context(CardRuntime runtime, string instanceId, LoadedCard card, CardSize size)
        {
            _runtime = runtime;
            InstanceId = instanceId;
            _card = card;
            _size = size;
        }

        public string InstanceId { get; }

        public Color Accent => Appearance.Theme.Accent;
        public double FontSize => Appearance.Theme.FontSize;
        public CardSize Size => _size;

        public void RefreshSize(CardSize size) => _size = size;

        public void RequestResize(CardSize size)
        {
            DsDock.Diagnostics.Log.Info($"卡片 {InstanceId} 请求改尺寸为 {size}");
            _runtime.ResizeRequested?.Invoke(InstanceId, size);
        }

        public void RequestRemove()
        {
            DsDock.Diagnostics.Log.Info($"卡片 {InstanceId} 请求移除（数据保留）");
            _runtime.RemoveRequested?.Invoke(InstanceId);
        }

        public void Log(string message) => DsDock.Diagnostics.Log.Info($"卡片[{InstanceId}] {message}");

        public bool Notify(string title, string message)
        {
            bool sent = _runtime.NotifyRequested?.Invoke(title, message) ?? false;
            DsDock.Diagnostics.Log.Info($"卡片 {InstanceId} 请求系统通知: {title} / {message} → {(sent ? "已发送" : "未发送")}");
            return sent;
        }

        public IEditorPopup ShowPopup(string title, FrameworkElement content)
            => _runtime.PopupRequested?.Invoke(title, content) ?? new NullPopup();

        public void SaveState(string json)
        {
            _runtime._state[InstanceId] = json;
            Storage.LayoutStore.SaveState(InstanceId, json);   // 落盘：重启后状态不丢
            DsDock.Diagnostics.Log.Info($"卡片 {InstanceId} 保存状态 {json.Length} 字节（已落盘 data/cards/{InstanceId}.json）");
        }

        public string LoadState()
        {
            string memory = _runtime.StateOf(InstanceId);
            if (memory.Length > 0) return memory;
            string fromDisk = Storage.LayoutStore.LoadState(InstanceId);
            if (fromDisk.Length > 0) _runtime._state[InstanceId] = fromDisk;
            return fromDisk;
        }
    }
}

/// <summary>容器里承载卡片视图的壳：1px 半透明白边 + 8px 圆角，按格子定位并支持多格尺寸。</summary>
internal sealed partial class CardHost : Border
{
    private readonly double _cellSize;
    private readonly double _gap;
    private readonly double _pad;
    private readonly Canvas _canvas;
    private readonly ICard _card;

    public CardHost(string instanceId, CardSize size, ICard card, Canvas canvas, double cellSize, double gap, double pad)
    {
        InstanceId = instanceId;
        Size = size;
        _card = card;
        _canvas = canvas;
        _cellSize = cellSize;
        _gap = gap;
        _pad = pad;

        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        ClipToBounds = true;
        Child = card.View;
        ApplySize(size);
        canvas.Children.Add(this);
    }

    public string InstanceId { get; }
    public CardSize Size { get; private set; }
    public bool IsErrorPlaceholder { get; private set; }
    public ICard Card => _card;

    public void ApplySize(CardSize size)
    {
        Size = size;
        Width = size.Columns * _cellSize + (size.Columns - 1) * _gap;
        Height = size.Rows * _cellSize + (size.Rows - 1) * _gap;
    }

    /// <summary>卡片库里的加载失败项也用它显示灰色占位。</summary>
    public void MarkAsError(string reason)
    {
        IsErrorPlaceholder = true;
        Background = new SolidColorBrush(Color.FromArgb(0x33, 0x80, 0x80, 0x80));
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x80, 0x80, 0x80));
        if (Child is StackPanel stack && stack.Children.Count > 0 && stack.Children[0] is TextBlock first)
            first.Text = reason;
    }

    public void SetCell(int col, int row)
    {
        Canvas.SetLeft(this, _pad + col * (_cellSize + _gap));
        Canvas.SetTop(this, _pad + row * (_cellSize + _gap));
        Cell = (col, row);
    }

    public (int Col, int Row) Cell { get; private set; }
}
