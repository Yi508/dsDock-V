using System.Windows;
using System.Windows.Media;

namespace DsDock.Card.Abstractions;

/// <summary>A card footprint inside the container grid, expressed in grid cells.</summary>
public sealed record CardSize(int Columns, int Rows)
{
    public static readonly CardSize OneByOne = new(1, 1);
    public static readonly CardSize TwoByOne = new(2, 1);
    public static readonly CardSize OneByTwo = new(1, 2);
    public static readonly CardSize TwoByTwo = new(2, 2);

    public static CardSize Parse(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "2x1" => TwoByOne,
        "1x2" => OneByTwo,
        "2x2" => TwoByTwo,
        _ => OneByOne,
    };

    public override string ToString() => $"{Columns}x{Rows}";
}

/// <summary>manifest.json of one card folder.</summary>
public sealed class CardManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Entry { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> AllowedSizes { get; set; } = new();
    public string DefaultSize { get; set; } = "1x1";
    public int MaxInstances { get; set; } = 1;
    public string MinAppVersion { get; set; } = "1.0.0";

    /// <summary>Returns null when valid, otherwise the reason (used for logs and the library placeholder).</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Id)) return "缺少 id";
        if (string.IsNullOrWhiteSpace(Name)) return "缺少 name";
        if (string.IsNullOrWhiteSpace(Entry)) return "缺少 entry（卡片程序集文件名）";
        if (AllowedSizes.Count == 0) return "allowedSizes 为空";
        foreach (string size in AllowedSizes)
        {
            if (size is not ("1x1" or "2x1" or "1x2" or "2x2")) return $"allowedSizes 含非法值 {size}";
        }
        if (!AllowedSizes.Contains(DefaultSize)) return $"defaultSize({DefaultSize}) 不在 allowedSizes 中";
        if (MaxInstances < 1) return "maxInstances 必须 ≥ 1";
        return null;
    }
}

/// <summary>What a card is allowed to ask the host for, and what the host tells it about itself.</summary>
public interface ICardContext
{
    /// <summary>Current accent colour (settings 里的强调色).</summary>
    Color Accent { get; }

    /// <summary>Current font size (settings 里 12–20px).</summary>
    double FontSize { get; }

    /// <summary>Footprint the card currently occupies.</summary>
    CardSize Size { get; }

    /// <summary>Ask the host to cycle/apply a new size (host enforces the layout constraints).</summary>
    void RequestResize(CardSize size);

    /// <summary>Ask the host to remove this card from the container (data is kept).</summary>
    void RequestRemove();

    /// <summary>Persist this card's own state (host writes data/cards/&lt;instanceId&gt;.json).</summary>
    void SaveState(string json);

    /// <summary>
    /// 把卡片自绘的内容放进宿主的弹出窗口（置顶、可拖动、点外/Esc 关闭、淡入上浮 150ms）。
    /// 窗口装饰归宿主，内容归卡片 —— 卡片不需要自己创建窗口。
    /// </summary>
    IEditorPopup ShowPopup(string title, FrameworkElement content);

    /// <summary>
    /// 通过 Windows 系统通知（托盘气泡）提示用户。卡片**不能**自己弹系统通知，
    /// 一律走宿主的通道（宿主持有托盘图标）。返回是否发送成功。
    /// </summary>
    bool Notify(string title, string message);

    /// <summary>写一行日志到宿主日志（data/logs）。卡片没有自己的日志文件，排查靠它。</summary>
    void Log(string message);

    /// <summary>Read back this card''s own state; empty string when nothing was stored yet.</summary>
    string LoadState();
}

/// <summary>One live card view. The host owns placement, border, drag and menus.</summary>
public interface ICard
{
    FrameworkElement View { get; }
    void OnAttached(ICardContext context);
    void OnSizeChanged(CardSize size);
    void OnDetached();

    /// <summary>卡片自己的右键菜单项；默认没有，卡片按需覆写。</summary>
    IReadOnlyList<CardMenuItem> GetMenuItems() => Array.Empty<CardMenuItem>();
}

/// <summary>宿主弹出窗口的句柄。卡片用它关闭自己打开的编辑界面。</summary>
public interface IEditorPopup
{
    void Close();
    bool IsOpen { get; }
}

/// <summary>卡片自己贡献的右键菜单项（宿主排在"更改尺寸/移除卡片"之前）。</summary>
public sealed record CardMenuItem(string Label, Action? Invoke, bool Checked = false);

/// <summary>Entry point a card assembly must expose (one public parameterless implementation).</summary>
public interface ICardFactory
{
    string Id { get; }
    ICard Create();
}
