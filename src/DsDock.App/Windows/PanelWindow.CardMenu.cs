using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using DsDock.Appearance;
using DsDock.Card.Abstractions;
using DsDock.Controls;
using DsDock.Diagnostics;
using DsDock.Layout;
using DsDock.Platform;
using DsDock.Plugins;

namespace DsDock.Windows;

/// <summary>
/// 卡片菜单与卡片尺寸变更：右键「更改尺寸（按 allowedSizes 轮换）/ 移除卡片」，
/// 以及"空间不足"提示（自绘菜单形式的轻提示，避免假装按钮没反应）。
/// </summary>
internal sealed partial class PanelWindow
{
    private DsMenu? _cardMenu;
    public PopupWindow? LastPopup { get; private set; }

    /// <summary>卡片请求的弹出窗口（编辑界面），位置贴近主界面左侧。</summary>
    private IEditorPopup OpenCardPopup(string title, FrameworkElement content)
    {
        LastPopup?.Close();
        var popup = new PopupWindow(title, content, _clock);
        NativeMethods.GetCursorPos(out var pt);
        popup.ShowNear(pt.X + 12, pt.Y + 12);   // 编辑界面在鼠标附近弹出
        LastPopup = popup;
        popup.Closed += (_, _) => { if (ReferenceEquals(LastPopup, popup)) LastPopup = null; };
        Log.Info($"卡片弹出编辑界面: {title}");
        return popup;
    }
    private DsMenu? _hintMenu;

    public DsMenu? LastCardMenu => _cardMenu;
    public IReadOnlyList<CardHost> Hosts => _hosts;
    public string? LastHint { get; private set; }

    /// <summary>拖动吸附的合法性判定：越界或与其它卡片重叠 → false（宿主据此回弹）。</summary>
    public bool TryMoveCardTo(CardHost host, int col, int row)
    {
        Placement? placement = _placements.FirstOrDefault(p => p.InstanceId == host.InstanceId);
        if (placement == null) return false;
        if (!LayoutEngine.CanPlace(_placements, col, row, placement.Columns, placement.Rows, _rows, host.InstanceId))
        {
            Log.Info($"卡片 {host.InstanceId} 目标格 ({col},{row}) 非法（越界或重叠）→ 回弹");
            return false;
        }

        _placements[_placements.IndexOf(placement)] = placement with { Col = col, Row = row };
        Log.Info($"卡片 {host.InstanceId} 吸附到格 ({col},{row})，容器: {HostSummary}");
        SaveLayout();
        return true;
    }

    public void ShowCardContextMenu(CardHost host)
    {
        NativeMethods.GetCursorPos(out var pt);
        ShowCardContextMenuAt(host, pt.X, pt.Y);
    }

    public void ShowCardContextMenuAt(CardHost host, int x, int y)
    {
        _cardMenu?.CloseMenu();
        var menu = new DsMenu(Theme.FontSize, Theme.Accent, (int)Theme.TokenDuration("MenuFadeMs", 100));
        IReadOnlyList<CardMenuItem> cardItems = host.Card.GetMenuItems();
        foreach (CardMenuItem item in cardItems)
            menu.AddItem(item.Checked ? "✓ " + item.Label : item.Label, item.Invoke);
        if (cardItems.Count > 0) menu.AddSeparator();
        CardSize? nextSize = NextSizeOf(host);
        menu.AddItem(nextSize == null ? $"更改尺寸（当前 {host.Size}）" : $"更改尺寸（{host.Size} → {nextSize}）",
            () => CycleCardSize(host));
        menu.AddSeparator();
        menu.AddItem("移除卡片", () => RemoveCardInstance(host.InstanceId));
        menu.ShowAt(x, y);
        _cardMenu = menu;
        Log.Info($"卡片菜单: {string.Join(" | ", menu.ItemLabels)}（{host.InstanceId} {host.Size}）");
    }

    /// <summary>按 manifest 的 allowedSizes 轮换到下一个放得下的尺寸。</summary>
    public bool CycleCardSize(CardHost host)
    {
        CardSize[] sizes = AllowedSizesOf(host.InstanceId);
        if (sizes.Length == 0) return false;

        int current = Array.IndexOf(sizes, host.Size);
        for (int i = 1; i <= sizes.Length; i++)
        {
            CardSize candidate = sizes[(current + i) % sizes.Length];
            if (candidate == host.Size) continue;
            if (TryApplyCardSize(host, candidate)) return true;
        }

        ShowPanelHint("空间不足，无法改尺寸");
        return false;
    }

    /// <summary>改尺寸：必要时先把挡位长到够用，再在新位置放好并做尺寸动画。</summary>
    public bool TryApplyCardSize(CardHost host, CardSize size)
    {
        Placement? placement = _placements.FirstOrDefault(p => p.InstanceId == host.InstanceId);
        if (placement == null) return false;

        List<Placement> others = _placements.Where(p => p.InstanceId != host.InstanceId).ToList();
        int? needed = LayoutEngine.RowsNeededToAdd(others, size.Columns, size.Rows, MaxRows);
        if (needed == null) return false;
        if (needed.Value > _rows) ApplyRows(needed.Value);

        (int Col, int Row)? slot = LayoutEngine.FindSlot(others, size.Columns, size.Rows, _rows);
        if (slot == null) return false;

        _placements[_placements.IndexOf(placement)] =
            placement with { Col = slot.Value.Col, Row = slot.Value.Row, Columns = size.Columns, Rows = size.Rows };

        host.AnimateSizeTo(size);
        host.SetCell(slot.Value.Col, slot.Value.Row);
        Runtime.UpdateSize(host.InstanceId, size);
        Log.Info($"卡片 {host.InstanceId} 尺寸改为 {size} @格({slot.Value.Col},{slot.Value.Row})（挡位 2×{_rows}）");
        SaveLayout();
        return true;
    }

    /// <summary>下一个候选尺寸（让菜单文案提前告诉用户点了会变成什么）。</summary>
    private CardSize? NextSizeOf(CardHost host)
    {
        CardSize[] sizes = AllowedSizesOf(host.InstanceId);
        if (sizes.Length < 2) return null;
        int current = Array.IndexOf(sizes, host.Size);
        return sizes[(current + 1) % sizes.Length];
    }

    private CardSize[] AllowedSizesOf(string instanceId)
    {
        string cardId = instanceId.Split('-')[0];
        CardManifest? manifest = Runtime.Find(cardId)?.Entry.Manifest;
        return manifest == null ? Array.Empty<CardSize>() : manifest.AllowedSizes.Select(CardSize.Parse).ToArray();
    }

    /// <summary>轻提示（"空间不足，无法添加"这类），用同一种自绘菜单，UI 统一。</summary>
    public void ShowPanelHint(string text)
    {
        LastHint = text;
        NativeMethods.GetCursorPos(out var pt);
        _hintMenu?.CloseMenu();
        var menu = new DsMenu(Theme.FontSize, Theme.Accent, (int)Theme.TokenDuration("MenuFadeMs", 100));
        menu.AddItem(text, null, enabled: false);
        menu.ShowAt(pt.X, pt.Y);
        _hintMenu = menu;
        Log.Info("提示: " + text);
    }

    public void CloseMenusForTest()
    {
        _cardMenu?.CloseMenu();
        _hintMenu?.CloseMenu();
    }
}
