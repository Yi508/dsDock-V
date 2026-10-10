using System;
using System.Collections.Generic;
using System.Linq;
using DsDock.Card.Abstractions;
using DsDock.Diagnostics;
using DsDock.Layout;
using DsDock.Plugins;
using DsDock.Storage;

namespace DsDock.Windows;

/// <summary>
/// 容器布局的落盘与恢复（M5）：layout.json 记录每张真实卡片的卡片类型 / 格子 / 尺寸，
/// 卡片自己的状态由 CardRuntime 经 LayoutStore 存到 data/cards/&lt;instanceId&gt;.json。
/// </summary>
internal sealed partial class PanelWindow
{
    /// <summary>把当前容器写进 layout.json（任何增删/移动/改尺寸/改挡位后调用）。</summary>
    public void SaveLayout()
    {
        if (_placements.Count == 0 && _loadInProgress) return;

        var file = new LayoutFile { Version = 1, PanelRows = _rows, Columns = _columns };
        foreach (Placement placement in _placements)   // 含"待展开恢复"的无宿主记录
        {
            (int col, int row) = _homePositions.TryGetValue(placement.InstanceId, out var home) ? home : (placement.Col, placement.Row);
            file.Cards.Add(new CardInstanceRecord
            {
                InstanceId = placement.InstanceId,
                CardId = CardIdOf(placement.InstanceId),
                Col = col,
                Row = row,
                Columns = placement.Columns,
                Rows = placement.Rows,
            });
        }

        LayoutStore.Save(file);
        Log.Info($"布局已保存: 挡位 {file.Columns}×{file.PanelRows}，卡片 {file.Cards.Count} 张");
    }

    private static string CardIdOf(string instanceId)
    {
        int dash = instanceId.LastIndexOf('-');
        return dash > 0 ? instanceId.Substring(0, dash) : instanceId;
    }

    private bool _loadInProgress;

    /// <summary>
    /// 启动时恢复：读 layout.json（损坏则整包回退备份），逐张重建卡片。
    /// 返回可读结果，供日志与自检使用。
    /// </summary>
    public string RestoreFromLayout()
    {
        _loadInProgress = true;
        LayoutFile layout = LayoutStore.Load(out string detail);
        string result = detail;

        if (detail.StartsWith("损坏", StringComparison.Ordinal))
        {
            // 数据损坏：先提示用户，再尝试整包回退
            Runtime.LoadFailureNotified?.Invoke("数据损坏", $"layout.json 无法读取，正在尝试从备份回退（{detail}）");
            bool restored = BackupService.TryRestoreLatest(out string restoreDetail);
            result += $"；{restoreDetail}";
            if (restored) layout = LayoutStore.Load(out string secondDetail).Pipe(_ => LayoutStore.Load(out secondDetail));
        }

        if (layout.PanelRows > 0) ApplyRows(layout.PanelRows);

        int created = 0, skipped = 0;
        foreach (CardInstanceRecord record in layout.Cards)
        {
            var size = new CardSize(record.Columns, record.Rows);
            ICard? card = Runtime.Create(record.CardId, record.InstanceId, size, out string error);
            if (card == null)
            {
                skipped++;
                Log.Info($"恢复卡片失败（跳过）: {record.InstanceId} — {error}");
                continue;
            }

            var placement = new Placement(record.InstanceId, record.Col, record.Row, record.Columns, record.Rows);
            _placements.Add(placement);
            AddHost(placement, card);
            created++;
        }

        // 让后续新增卡片的编号不与恢复出来的实例冲突
        foreach (CardInstanceRecord record in layout.Cards)
        {
            int dash = record.InstanceId.LastIndexOf('-');
            if (dash > 0 && int.TryParse(record.InstanceId.Substring(dash + 1), out int seq))
                _cardSeq = Math.Max(_cardSeq, seq);
        }

        // 收回态启动：按本位建卡后，把扩展列的临时挪进 2 列（放不下的保持隐藏待恢复）
        if (_columns == 2 && _placements.Any(p => p.Col + p.Columns > _columns))
        {
            RelocateExtCards();
            Log.Info($"收回态启动重排: 可见 {_hosts.Count} 张，待恢复 {PendingCount} 张");
        }

        _loadInProgress = false;
        result += $"；已恢复 {created} 张卡片" + (skipped > 0 ? $"，跳过 {skipped} 张" : "");
        if (PendingCount > 0) result += $"，{PendingCount} 张待展开恢复";
        return result;   // 调用方（AppShell）负责记录日志，避免重复行
    }

    /// <summary>自检用：当前容器的布局快照。</summary>
    public LayoutFile LayoutSnapshotForTest()
    {
        var file = new LayoutFile { PanelRows = _rows, Columns = _columns };
        foreach (Placement placement in _placements)   // 含"待展开恢复"的无宿主记录
        {
            (int col, int row) = _homePositions.TryGetValue(placement.InstanceId, out var home) ? home : (placement.Col, placement.Row);
            file.Cards.Add(new CardInstanceRecord
            {
                InstanceId = placement.InstanceId,
                CardId = CardIdOf(placement.InstanceId),
                Col = col,
                Row = row,
                Columns = placement.Columns,
                Rows = placement.Rows,
            });
        }
        return file;
    }

    /// <summary>自检用：清空容器并删掉对应状态文件（模拟"全新安装"）。</summary>
    public void ClearAllForTest()
    {
        foreach (CardHost host in _hosts.ToList()) LayoutStore.DeleteState(host.InstanceId);
        ClearAllCards();
    }
}

internal static class PipeExtensions
{
    public static TResult Pipe<T, TResult>(this T value, Func<T, TResult> map) => map(value);
}
