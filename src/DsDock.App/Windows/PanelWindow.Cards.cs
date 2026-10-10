using System;
using System.Collections.Generic;
using System.Linq;
using DsDock.Appearance;
using DsDock.Card.Abstractions;
using DsDock.Diagnostics;
using DsDock.Layout;
using DsDock.Plugins;

namespace DsDock.Windows;

/// <summary>
/// 主界面的"卡片实例"部分（与窗口装配分离，便于单测与自检）。
/// 只负责：布局约束（找空位/自动扩行）→ 建 CardHost → 维护 _placements。
/// </summary>
internal sealed partial class PanelWindow
{
    private readonly List<CardHost> _hosts = new();

    public CardRuntime Runtime { get; } = new();

    public int HostCount => _hosts.Count;

    /// <summary>卡片库用：容器里该卡片的实例数。</summary>
    public int CountOf(string cardId) => Runtime.CountOf(cardId);

    public bool CanAddCard(string cardId, out string reason) => Runtime.CanAdd(cardId, out reason);

    /// <summary>添加一张真实卡片：先满足布局（必要时自动扩行），再隔离加载并挂到容器。</summary>
    public bool AddCard(string cardId, CardSize? requestedSize = null)
    {
        if (!Runtime.CanAdd(cardId, out string reason))
        {
            Log.Info($"添加卡片被拒: {reason}");
            return false;
        }

        LoadedCard card = Runtime.Find(cardId)!;
        CardSize size = requestedSize ?? CardSize.Parse(card.Entry.Manifest!.DefaultSize);
        if (!card.Entry.Manifest!.AllowedSizes.Contains(size.ToString())) size = CardSize.Parse(card.Entry.Manifest.DefaultSize);

        int? neededRows = LayoutEngine.RowsNeededToAdd(_placements, size.Columns, size.Rows, MaxRows, _columns);
        if (neededRows == null)
        {
            Log.Info($"空间不足，无法添加（本屏上限 2×{MaxRows} 仍放不下）");
            ShowPanelHint("空间不足，无法添加");
            return false;
        }
        if (neededRows.Value > _rows) ApplyRows(neededRows.Value);

        var slot = LayoutEngine.FindSlot(_placements, size.Columns, size.Rows, _rows, gridColumns: _columns);
        if (slot == null)
        {
            Log.Info("空间不足，无法添加（找不到空位）");
            ShowPanelHint("空间不足，无法添加");
            return false;
        }

        string instanceId = $"{cardId}-{++_cardSeq}";
        ICard? instance = Runtime.Create(cardId, instanceId, size, out string error);
        if (instance == null)
        {
            Log.Info("添加卡片失败: " + error);
            return false;
        }

        var placement = new Placement(instanceId, slot.Value.Col, slot.Value.Row, size.Columns, size.Rows);
        _placements.Add(placement);
        AddHost(placement, instance);
        Log.Info($"已添加卡片 {cardId}（{instanceId}）尺寸 {size} 到格 ({placement.Col},{placement.Row})，挡位 {_columns}×{_rows}");
        SaveLayout();
        return true;
    }

    /// <summary>移除容器里的最后一张该卡片（数据保留在运行时）。</summary>
    public bool RemoveCard(string cardId)
    {
        CardHost? host = _hosts.LastOrDefault(h => h.InstanceId.StartsWith(cardId + "-", StringComparison.Ordinal));
        if (host == null) return false;
        RemoveHost(host);
        Log.Info($"已从容器移除卡片 {cardId}（{host.InstanceId}），数据保留");
        return true;
    }

    public bool RemoveCardInstance(string instanceId)
    {
        CardHost? host = _hosts.FirstOrDefault(h => h.InstanceId == instanceId);
        if (host == null) return false;
        RemoveHost(host);
        return true;
    }

    private void RemoveHost(CardHost host)
    {
        host.Card.OnDetached();
        _hosts.Remove(host);
        _placements.RemoveAll(p => p.InstanceId == host.InstanceId);
        Runtime.Remove(host.InstanceId);
        host.PlayDisappear(() => _canvas?.Children.Remove(host));
        ClearedCards?.Invoke();
        SaveLayout();
    }

    private void AddHost(Placement placement, ICard card)
    {
        if (_canvas == null) return;
        var host = new CardHost(placement.InstanceId, new CardSize(placement.Columns, placement.Rows), card,
            _canvas, CellSize, Gap, Pad);
        host.SetCell(placement.Col, placement.Row);
        host.Clock = _clock;
        host.TryMoveTo = TryMoveCardTo;
        host.ContextMenuRequested = ShowCardContextMenu;
        _hosts.Add(host);
        host.PlayAppear();
    }

    /// <summary>卡片库里的加载失败项在容器外只做灰色占位展示（此处仅记录）。</summary>
    public string CardRuntimeDetail => Runtime.RegistryDetail;

    public string HostSummary => _hosts.Count == 0
        ? "(容器内无卡片)"
        : string.Join(", ", _hosts.Select(h => $"{h.InstanceId}@({h.Cell.Col},{h.Cell.Row}) {h.Size}"));

    /// <summary>清空容器（含真实卡片），保留数据。</summary>
    public void ClearAllCards()
    {
        foreach (CardHost host in _hosts.ToList())
        {
            host.Card.OnDetached();
            _canvas?.Children.Remove(host);
            Runtime.Remove(host.InstanceId);
        }
        _hosts.Clear();
        ClearCards();   // 占位卡片 + 归档
    }

    /// <summary>把某种卡片的所有实例从容器移除（移除整张卡片时调用），返回移除数量。</summary>
    public int RemoveAllCardsOf(string cardId)
    {
        var targets = _hosts.Where(h => h.InstanceId.StartsWith(cardId + "-", StringComparison.Ordinal)).ToList();
        foreach (CardHost host in targets)
        {
            host.Card.OnDetached();
            _canvas?.Children.Remove(host);
            Runtime.Remove(host.InstanceId);
            _hosts.Remove(host);
            _placements.RemoveAll(p => p.InstanceId == host.InstanceId);
        }

        // 连"待展开恢复"（无宿主）的布局记录一起清掉，卸载卡片类型后不留孤儿
        int orphanRecords = _placements.RemoveAll(p => p.InstanceId.StartsWith(cardId + "-", StringComparison.Ordinal));
        foreach (string key in _homePositions.Keys.Where(k => k.StartsWith(cardId + "-", StringComparison.Ordinal)).ToList())
            _homePositions.Remove(key);

        if (targets.Count > 0 || orphanRecords > 0)
        {
            SaveLayout();
            Log.Info($"已从容器移除 {cardId}: 在容器 {targets.Count} 张，另清理待恢复记录 {orphanRecords} 条");
        }
        return targets.Count;
    }

    /// <summary>外观变化时同步给所有卡片实例。</summary>
    private void NotifyCardsTheme()
    {
        Runtime.ApplyTheme(Theme.Accent, Theme.FontSize);
        foreach (CardHost host in _hosts)
        {
            if (host.Size.Columns <= 0) continue;
            Runtime.UpdateSize(host.InstanceId, host.Size);
        }
    }

    /// <summary>把格坐标换算成屏幕坐标（自检用）。</summary>
    public (int X, int Y) CellOriginOnScreen(int col, int row)
    {
        var rect = PanelRect();
        double scale = _dpiScale;
        int x = rect.Left + (int)Math.Round((Pad + col * (CellSize + Gap)) * scale);
        int y = rect.Top + (int)Math.Round((TopBarHeight + Pad + row * (CellSize + Gap)) * scale);
        return (x, y);
    }
}
