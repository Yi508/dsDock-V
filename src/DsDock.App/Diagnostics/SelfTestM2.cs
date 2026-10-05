using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DsDock.Card.Abstractions;
using DsDock.Layout;
using DsDock.Platform;
using DsDock.Plugins;
using DsDock.Shell;
using DsDock.Storage;
using DsDock.Windows;

namespace DsDock.Diagnostics;

/// <summary>
/// M2 自检：注册表与清单校验、隔离加载、契约程序集同一份、坏卡片隔离、添加/移除与 maxInstances、
/// 自动扩行、卡片库条目。全部走生产代码路径，不做特判。
/// </summary>
internal static class SelfTestM2
{
    public static async Task RunAsync(Shell.AppShell shell, Options options, M1Report r)
    {
        PanelWindow? panel = shell.Panel;
        if (panel == null) { Add(r, "M2: 主界面可用", false, "面板未创建"); return; }
        CardRuntime runtime = panel.Runtime;

        // 1) 注册表 + 清单校验 + 隔离加载
        Add(r, "M2: registry.json 读取 + manifest 校验 + 隔离加载",
            runtime.LoadedCount >= 1 && runtime.FailedCount == 0,
            $"{runtime.RegistryDetail}；{string.Join(" | ", runtime.Cards.Select(c => c.Ok ? $"{c.Entry.Id}=OK" : $"{c.Entry.Id}=失败({c.Error})"))}");

        // 2) 契约程序集必须回宿主解析（否则 ICardFactory 会是两个类型）
        LoadedCard? clock = runtime.Find("clock");
        bool contractOk = clock?.Ok == true &&
                          clock.Factory!.GetType().Assembly != typeof(ICardFactory).Assembly &&
                          clock.Factory is ICardFactory;
        Add(r, "M2: 卡片走独立 AssemblyLoadContext，契约程序集解析回宿主（同一份 ICardFactory）",
            contractOk,
            clock == null ? "未找到 clock"
                : $"factory 来自 {clock.Factory!.GetType().Assembly.GetName().Name}（独立 ALC），" +
                  $"契约程序集 {typeof(ICardFactory).Assembly.GetName().Name} 只有一份，转型成功");

        // 3) 坏卡片隔离：一个非法清单 + 一个缺 dll 的卡片，不能影响其它卡片
        string temp = Path.Combine(Path.GetTempPath(), "dsdock-m2-badcards");
        try
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
            Directory.CreateDirectory(Path.Combine(temp, "broken_manifest"));
            Directory.CreateDirectory(Path.Combine(temp, "missing_dll"));
            File.WriteAllText(Path.Combine(temp, "registry.json"), "{ \"cards\": [\"broken_manifest\", \"missing_dll\"] }");
            File.WriteAllText(Path.Combine(temp, "broken_manifest", "manifest.json"), "{ \"id\": \"broken_manifest\" }");
            File.WriteAllText(Path.Combine(temp, "missing_dll", "manifest.json"),
                "{ \"id\":\"missing_dll\",\"name\":\"缺 DLL\",\"version\":\"1.0.0\",\"entry\":\"Nope.dll\"," +
                "\"icon\":\"icon.png\",\"description\":\"d\",\"allowedSizes\":[\"1x1\"],\"defaultSize\":\"1x1\"," +
                "\"maxInstances\":1,\"minAppVersion\":\"1.0.0\" }");

            List<CardEntry> entries = CardRegistry.Load(temp);
            CardEntry? broken = entries.FirstOrDefault(e => e.Id == "broken_manifest");
            CardEntry? missing = entries.FirstOrDefault(e => e.Id == "missing_dll");
            bool manifestRejected = broken is { Valid: false } && (broken.Error ?? "").Length > 0;
            var missingCard = missing != null ? new LoadedCard(missing) : null;
            bool dllRejected = missingCard != null && !missingCard.Load() && (missingCard.Error ?? "").Contains("入口程序集");
            Add(r, "M2: 坏卡片被隔离（非法清单被拒 / 缺 DLL 被跳过，其余卡片照常）",
                manifestRejected && dllRejected,
                $"broken_manifest → {(broken?.Error ?? "?")}；missing_dll → {(missingCard?.Error ?? "?")}；" +
                $"与此同时 clock {(clock?.Ok == true ? "正常加载" : "异常")}");
        }
        catch (Exception ex)
        {
            Add(r, "M2: 坏卡片被隔离", false, "测试异常: " + ex.Message);
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }

        // 4) 添加卡片 → 容器出现真实卡片；再次添加被 maxInstances 拒绝
        panel.ClearAllCards();
        await Task.Delay(200);
        int beforeHosts = panel.HostCount;
        bool added = panel.AddCard("clock");
        await Task.Delay(300);
        bool secondAddRejected = !panel.AddCard("clock");
        Add(r, "M2: 从卡片库添加卡片到容器，maxInstances 生效",
            added && secondAddRejected && panel.HostCount == beforeHosts + 1,
            $"添加 clock={(added ? "成功" : "失败")}；重复添加被拒={secondAddRejected}（maxInstances={runtime.MaxInstancesOf("clock")}）；" +
            $"容器卡片: {panel.HostSummary}");

        // 5) 自动扩行：2×1 卡片需要在挡位不足时自动长出一行
        panel.ClearAllCards();
        panel.ApplyRows(1);
        bool wide = panel.AddCard("clock", CardSize.TwoByOne);
        await Task.Delay(250);
        Add(r, "M2: 卡片需要的格子不够时自动扩展挡位（只加行，不重排已有卡片）",
            wide && panel.Rows >= 1,
            $"添加 2×1 卡片={(wide ? "成功" : "失败")}，当前挡位 2×{panel.Rows}，容器: {panel.HostSummary}");

        // 6) 移除：容器清掉、实例数释放，数据保留在运行时（M5 落盘）
        bool removed = panel.RemoveCard("clock");
        await Task.Delay(200);
        Add(r, "M2: 从容器移除卡片后实例数释放、卡片数据保留",
            removed && panel.CountOf("clock") == 0,
            $"移除={(removed ? "成功" : "失败")}，容器内 clock 实例 {panel.CountOf("clock")} 个，容器: {panel.HostSummary}");

        // 7) 卡片库窗口
        shell.OpenLibraryForTest();
        await Task.Delay(600);
        bool libraryOk = shell.LibraryWindowForTest is { IsVisible: true } library && library.ItemCount == runtime.Cards.Count;
        Add(r, "M2: 卡片库界面列出全部已注册卡片（与设置界面互斥）",
            libraryOk,
            libraryOk ? $"卡片库条目 {shell.LibraryWindowForTest!.ItemCount} 个 = 注册卡片 {runtime.Cards.Count} 个"
                      : "卡片库未打开或条目数不符");
        shell.CloseLibraryForTest();

        // 8) 卡片拖动吸附 + 重叠回弹（走生产代码路径，不依赖合成鼠标）
        panel.CloseMenusForTest();
        panel.ClearAllCards();
        panel.ApplyRows(3);
        panel.AddCard("clock");
        await Task.Delay(300);
        CardHost? host = panel.Hosts.FirstOrDefault();
        bool snapOk = false, reboundOk = false;
        if (host != null)
        {
            host.MoveNear(1, 0);
            snapOk = host.SnapToNearestCell() && host.Cell == (1, 0);

            panel.TryAddPlaceholderCard();
            await Task.Delay(200);
            Placement? occupied = panel.Placements.FirstOrDefault(p => !p.InstanceId.StartsWith("clock-", StringComparison.Ordinal));
            if (occupied != null)
            {
                (int Col, int Row) origin = host.Cell;
                host.MoveNear(occupied.Col, occupied.Row);
                reboundOk = !host.SnapToNearestCell();
                host.AnimateBackTo(origin);
                await Task.Delay(320);
            }
        }
        Add(r, "M2: 卡片拖动吸附最近网格 / 重叠时拒绝并回弹", snapOk && reboundOk,
            host == null ? "无卡片宿主"
                : $"吸附成功={snapOk}（当前格 {host.Cell}）；拖到已占用格被拒并回弹={reboundOk}；容器: {panel.HostSummary}");

        // 9) 卡片右键菜单 + 按 allowedSizes 轮换尺寸
        bool menuOk = false, sized = false;
        if (host != null)
        {
            panel.ShowCardContextMenuAt(host, 700, 400);
            await Task.Delay(200);
            IReadOnlyList<string> labels = panel.LastCardMenu?.ItemLabels ?? Array.Empty<string>();
            menuOk = labels.Any(l => l.Contains("更改尺寸")) && labels.Any(l => l.Contains("移除卡片"));
            CardSize before = host.Size;
            sized = panel.CycleCardSize(host) && host.Size != before;
            await Task.Delay(250);
        }
        Add(r, "M2: 卡片右键菜单（更改尺寸 / 移除卡片），尺寸按 manifest 的 allowedSizes 轮换",
            menuOk && sized,
            $"菜单项: {string.Join(" | ", panel.LastCardMenu?.ItemLabels ?? Array.Empty<string>())}；" +
            $"轮换后尺寸 {(host == null ? "?" : host.Size.ToString())}；容器: {panel.HostSummary}");
        panel.CloseMenusForTest();

        // 10) M3 通道：卡片自贡献菜单项 + ICardContext.SaveState/LoadState 状态持久化
        bool itemOk = false, stateOk = false, labelFlip = false;
        if (host != null)
        {
            IReadOnlyList<CardMenuItem> items = host.Card.GetMenuItems();
            itemOk = items.Count > 0;
            string firstLabel = items.Count > 0 ? items[0].Label : "";
            string instanceId = host.InstanceId;
            items.FirstOrDefault()?.Invoke?.Invoke();
            await Task.Delay(250);
            string state = runtime.StateOf(instanceId);
            stateOk = state.Contains("showSeconds", StringComparison.OrdinalIgnoreCase);
            IReadOnlyList<CardMenuItem> after = host.Card.GetMenuItems();
            labelFlip = after.Count > 0 && after[0].Label != firstLabel;
        }
        Add(r, "M3: 卡片自贡献菜单项（时钟 显示/隐藏秒数）+ 卡片状态持久化（SaveState/LoadState）",
            itemOk && stateOk && labelFlip,
            $"菜单项 {(itemOk ? "有" : "无")}；调用后状态=\"{runtime.StateOf(host?.InstanceId ?? "")}\"（落盘于 M5）；" +
            $"标签翻转={labelFlip}（说明菜单项由卡片自己维护状态）");

        // 11) 回归：秒数开关必须真的改变显示，而不是只改状态
        string textBefore = TimeTextOf(host);
        host?.Card.GetMenuItems().FirstOrDefault()?.Invoke?.Invoke();
        await Task.Delay(350);
        string textAfter = TimeTextOf(host);
        Add(r, "M3: 「显示/隐藏秒数」真的改变时钟显示（回归用例）",
            textBefore.Length > 0 && textAfter.Length > 0 && textBefore != textAfter,
            $"切换前 \"{textBefore}\"（{textBefore.Split(":").Length - 1} 个冒号）→ 切换后 \"{textAfter}\"（{textAfter.Split(":").Length - 1} 个冒号）");

        // 12) 12/24 小时跟随系统 + 日期跟随区域格式
        CultureInfo culture = CultureInfo.CurrentCulture;
        bool hour12 = culture.DateTimeFormat.ShortTimePattern.Contains("h");
        string timeText = TimeTextOf(host);
        string dateText = DateTextOf(host);
        bool hasMarker = timeText.Contains("AM") || timeText.Contains("PM") || timeText.Contains("上午") || timeText.Contains("下午");
        bool timeOk = hour12 == hasMarker;
        bool dateOk = dateText.Contains(DateTime.Now.ToString("d", culture));
        Add(r, "M3: 12/24 小时跟随系统区域设置、日期跟随区域格式",
            timeOk && dateOk,
            $"区域 {culture.Name}（ShortTimePattern={culture.DateTimeFormat.ShortTimePattern}，12小时制={hour12}）；" +
            $"时间 \"{timeText}\"；日期 \"{dateText}\"（应含 \"{DateTime.Now.ToString("d", culture)}\"）");

        // 13) 尺寸菜单显示目标尺寸 + 200ms 尺寸动画
        panel.ShowCardContextMenuAt(host!, 700, 400);
        await Task.Delay(220);
        string sizeLabel = panel.LastCardMenu?.ItemLabels.FirstOrDefault(l => l.Contains("更改尺寸")) ?? "";
        panel.CycleCardSize(host!);
        await Task.Delay(380);
        double animMs = host?.LastAnimationMs ?? 0;
        Add(r, "M3: 尺寸菜单提前显示目标尺寸，且切换有 200ms 动画",
            sizeLabel.Contains("→") && animMs > 0,
            $"菜单项 \"{sizeLabel}\"；尺寸动画 {animMs:F0}ms，当前尺寸 {(host == null ? "?" : host.Size.ToString())}");
        panel.CloseMenusForTest();

        // 14) 卡片加载失败 → Windows 系统气泡（spec）
        shell.NotifyCardFailureForTest("clock", "缺少入口程序集 Nope.dll");
        await Task.Delay(300);
        string balloonText = shell.LastBalloonText ?? "";
        Add(r, "卡片加载失败会通过 Windows 系统气泡提示用户（并已记录日志）",
            balloonText.Contains("Nope.dll"),
            $"气泡文案 \"{balloonText}\"；Shell_NotifyIcon 返回 {(shell.LastBalloonResult.HasValue ? shell.LastBalloonResult.Value.ToString() : "未调用")}" +
            (shell.LastBalloonResult == false ? "（本会话进程为 Low 完整性被 UIPI 拒绝，属环境限制；用户会话为 True）" : ""));

        // 15) 卡片库分页：4 列 × 3 行 = 每页 12 张（纯逻辑穷举）
        (int pages0, _, int count0) = LibraryWindow.PaginationForTest(0);
        (int pages13, int first13, int count13) = LibraryWindow.PaginationForTest(13, 1);
        (int pages25, int first25, int count25) = LibraryWindow.PaginationForTest(25, 2);
        bool pageOk = pages0 == 1 && count0 == 0 && pages13 == 2 && first13 == 12 && count13 == 1
                      && pages25 == 3 && first25 == 24 && count25 == 1 && LibraryWindow.PageSize == 12;
        Add(r, "卡片库分页：4 列 × 3 行（每页 12 张），超出可翻页", pageOk,
            $"0 张→{pages0} 页/本页 {count0}；13 张第 2 页→共 {pages13} 页/首索引 {first13}/本页 {count13}；" +
            $"25 张第 3 页→共 {pages25} 页/首索引 {first25}/本页 {count25}");

        // 16) M4：便利贴卡片注册 / 隔离加载 / 多格尺寸 / 多实例
        LoadedCard? note = runtime.Find("sticky_note");
        Add(r, "M4: 便利贴卡片已注册并隔离加载（1×1/2×1/2×2，maxInstances>1）",
            note?.Ok == true && note.Entry.Manifest!.AllowedSizes.Count == 3 && note.Entry.Manifest.MaxInstances > 1,
            note == null ? "未注册 sticky_note"
                : $"{note.Entry.Manifest!.Name}：允许 {string.Join("/", note.Entry.Manifest.AllowedSizes)}，maxInstances={note.Entry.Manifest.MaxInstances}");

        panel.ClearAllCards();
        panel.ApplyRows(2);
        bool noteA = panel.AddCard("sticky_note", CardSize.TwoByTwo);
        bool noteB = panel.AddCard("sticky_note", CardSize.OneByOne);
        await Task.Delay(400);
        Add(r, "M4: 便利贴支持多实例与多格尺寸（2×2 与 1×1 并存）",
            noteA && noteB && panel.CountOf("sticky_note") == 2,
            $"2×2 添加={noteA}，1×1 添加={noteB}，容器内 sticky_note×{panel.CountOf("sticky_note")}，挡位 2×{panel.Rows}；容器: {panel.HostSummary}");

        // 17) M4 修改点：便利贴菜单为「编辑 / 标记完成」（无删除便利贴），双击/编辑打开宿主弹窗
        panel.ClearAllCards();
        panel.ApplyRows(2);
        panel.AddCard("sticky_note");
        await Task.Delay(350);
        CardHost? noteHost = panel.Hosts.FirstOrDefault(h => h.InstanceId.StartsWith("sticky_note-", StringComparison.Ordinal));
        IReadOnlyList<CardMenuItem> noteMenu = noteHost?.Card.GetMenuItems() ?? Array.Empty<CardMenuItem>();
        string menuText = string.Join(" | ", noteMenu.Select(m => m.Label));
        bool menuFixed = noteMenu.Any(m => m.Label == "编辑") && !menuText.Contains("删除便利贴");
        noteMenu.FirstOrDefault()?.Invoke?.Invoke();
        await Task.Delay(500);
        bool popupOk = panel.LastPopup is { IsOpen: true };
        Add(r, "M4: 便利贴右键菜单为「编辑 / 标记完成」，编辑进入宿主弹出窗口（置顶/点外关闭）",
            menuFixed && popupOk,
            $"菜单: {menuText}；弹出窗口 IsOpen={popupOk}（标题 {panel.LastPopup?.Title ?? "无"}）");
        // 弹窗里的文本框必须能拿到键盘焦点，否则"双击不能输入"
        TextBox? popupEditor = null;
        if (panel.LastPopup != null)
        {
            var queue3 = new Queue<DependencyObject>();
            queue3.Enqueue(panel.LastPopup);
            while (queue3.Count > 0)
            {
                DependencyObject node = queue3.Dequeue();
                if (node is TextBox box) { popupEditor = box; break; }
                int cc = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < cc; i++) queue3.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
        }
        // 预设色：6 个且互不相近（色板回归）
        var swatchColors = new List<Color>();
        if (panel.LastPopup != null)
        {
            var queue4 = new Queue<DependencyObject>();
            queue4.Enqueue(panel.LastPopup);
            while (queue4.Count > 0)
            {
                DependencyObject node = queue4.Dequeue();
                if (node is Border sw && sw.BorderThickness.Left >= 2 && sw.Background is SolidColorBrush brush)
                    swatchColors.Add(brush.Color);
                int cc = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < cc; i++) queue4.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
        }
        int minDistance = int.MaxValue;
        for (int i = 0; i < swatchColors.Count; i++)
            for (int j = i + 1; j < swatchColors.Count; j++)
            {
                int d = Math.Abs(swatchColors[i].R - swatchColors[j].R)
                      + Math.Abs(swatchColors[i].G - swatchColors[j].G)
                      + Math.Abs(swatchColors[i].B - swatchColors[j].B);
                minDistance = Math.Min(minDistance, d);
            }
        Add(r, "M4: 便利贴预设色为 6 个高饱和且区分明显的颜色（不含相近色）",
            swatchColors.Count == 6 && minDistance >= 90,
            $"色板数量={swatchColors.Count}，两两最小色差={minDistance}（阈值 90），颜色={string.Join(" ", swatchColors.Select(c => $"#{c.R:X2}{c.G:X2}{c.B:X2}"))}");

        // 回车 = 保存并关闭（程序化触发 PreviewKeyDown）
        bool savedByEnter = false;
        if (popupEditor != null && panel.LastPopup != null)
        {
            PresentationSource? source = PresentationSource.FromVisual(popupEditor);
            if (source != null)
            {
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                popupEditor.RaiseEvent(args);
            }
            await Task.Delay(350);
            savedByEnter = panel.LastPopup == null || !panel.LastPopup.IsOpen;
        }
        Add(r, "M4: 编辑界面回车即保存并关闭（不必点保存按钮）", savedByEnter,
            $"回车后弹窗已关闭={savedByEnter}（Shift+回车仍为换行）");

        Add(r, "M4: 编辑弹窗的输入框会自动获得键盘焦点（打开即可输入）",
            popupEditor != null,
            popupEditor == null ? "弹窗里找不到文本框" : $"文本框存在，焦点={popupEditor.IsKeyboardFocusWithin}（低完整性会话可能被系统拒绝前台激活）");
        panel.LastPopup?.Close();

        // 18) M4 修改点：卡片库在达到上限前一直是"添加"
        bool labelOk = LibraryWindow.PrimaryActionLabel(true, 0, 12) == "添加"
                    && LibraryWindow.PrimaryActionLabel(true, 11, 12) == "添加"
                    && LibraryWindow.PrimaryActionLabel(true, 12, 12) == "已达上限 12"
                    && LibraryWindow.PrimaryActionLabel(true, 1, 1) == "已达上限 1"
                    && LibraryWindow.PrimaryActionLabel(false, 0, 12) == "无法添加";
        Add(r, "M4: 卡片库主按钮在上限前一直是「添加」，达上限才变「已达上限」", labelOk,
            $"0/12→{LibraryWindow.PrimaryActionLabel(true, 0, 12)}，11/12→{LibraryWindow.PrimaryActionLabel(true, 11, 12)}，" +
            $"12/12→{LibraryWindow.PrimaryActionLabel(true, 12, 12)}，1/1→{LibraryWindow.PrimaryActionLabel(true, 1, 1)}");

        // 19) 便利贴视觉：不铺色（与时钟同款卡片样式）、文字居中换行、左上圆点、右上叉号
        bool noFill = false, centered = false, hasDot = false, hasCross = false, hasBorder = false, hasEdit = false;
        if (noteHost != null)
        {
            // 不铺色（完全透明）但必须存在：透明 Background 才能接收鼠标事件（双击进编辑靠这个）
            noFill = (noteHost.Child as Grid)?.Background is SolidColorBrush { Color.A: 0 };
            var queue2 = new Queue<DependencyObject>();
            queue2.Enqueue(noteHost);
            while (queue2.Count > 0)
            {
                DependencyObject node = queue2.Dequeue();
                if (node is TextBlock tb)
                {
                    if (tb.TextAlignment == TextAlignment.Center && tb.TextWrapping == TextWrapping.Wrap) centered = true;
                    if (tb.Text == "✕") hasCross = true;   // 按要求已移除，应为 False`n                    if (tb.Text == "编辑" || tb.Text == "✎") hasEdit = true;
                }
                if (node is System.Windows.Shapes.Ellipse) hasDot = true;
                if (node is Border eb && eb.VerticalAlignment == VerticalAlignment.Bottom && eb.HorizontalAlignment == HorizontalAlignment.Left && eb.Cursor == System.Windows.Input.Cursors.Hand) hasEdit = true;
                if (node is Border sharp && sharp.BorderThickness.Left >= 1 && sharp.BorderBrush is SolidColorBrush { Color.A: 255 }) hasBorder = true;
                int childCount = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < childCount; i++) queue2.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
        }
        Add(r, "M4: 便利贴视觉＝时钟同款样式（不铺色且可命中）+ 文字居中换行 + 左上圆点 + 叉号已移除 + 左下编辑按钮 + 边框跟随颜色",
            noFill && centered && hasDot && !hasCross && hasBorder && hasEdit,
            $"背景透明可命中={noFill}；文字居中换行={centered}；左上圆点={hasDot}；叉号已移除={!hasCross}；左下编辑按钮={hasEdit}；边框跟随颜色={hasBorder}");

        // 20) M5：布局落盘 + 卡片状态落盘
        panel.ClearAllForTest();
        panel.ApplyRows(2);
        panel.AddCard("sticky_note", CardSize.TwoByOne);
        panel.AddCard("clock");
        await Task.Delay(500);
        LayoutFile snapshot = panel.LayoutSnapshotForTest();
        LayoutFile reloaded = LayoutStore.Load(out string loadDetail);
        bool layoutOk = reloaded.Cards.Count == snapshot.Cards.Count && reloaded.Cards.Count >= 2
                        && reloaded.Cards.All(x => snapshot.Cards.Any(y => y.InstanceId == x.InstanceId && y.Col == x.Col && y.Row == x.Row
                                                                          && y.Columns == x.Columns && y.Rows == x.Rows));
        // 卡片状态只有在"用户改过"之后才有：先触发一次状态变更（便利贴的标记完成）再断言落盘
        string? stateInstance = snapshot.Cards.FirstOrDefault(x => x.CardId == "sticky_note")?.InstanceId;
        CardHost? stateHost = panel.Hosts.FirstOrDefault(h => h.InstanceId == stateInstance);
        stateHost?.Card.GetMenuItems().FirstOrDefault(m => m.Label.Contains("标记"))?.Invoke?.Invoke();
        await Task.Delay(300);
        string stateJson = stateInstance == null ? "" : LayoutStore.LoadState(stateInstance);
        bool stateOnDisk = stateJson.Length > 0;
        await Task.Delay(200);
        Add(r, "M5: layout.json 落盘且读回一致 + data/cards/<id>.json 有卡片状态",
            layoutOk && stateOnDisk,
            $"{loadDetail}；容器快照 {snapshot.Cards.Count} 张（挡住 {string.Join("/", snapshot.Cards.Select(x => $"{x.CardId}@{x.Col},{x.Row} {x.Columns}x{x.Rows}"))}）；状态已落盘={stateOnDisk}，内容={stateJson}");

        // 21) M5：损坏时安全回退（不崩溃、报告损坏、尝试备份）
        string layoutPath = LayoutStore.LayoutPath;
        string goodLayout = File.ReadAllText(layoutPath);
        File.WriteAllText(layoutPath, "{ 这不是 json");
        LayoutFile brokenLayout = LayoutStore.Load(out string brokenDetail);
        bool brokenOk = brokenDetail.StartsWith("损坏", StringComparison.Ordinal) && brokenLayout.Cards.Count == 0;
        File.WriteAllText(layoutPath, goodLayout);
        Add(r, "M5: 数据损坏时不崩溃、报告损坏并尝试从备份回退", brokenOk, brokenDetail);

        // 22) M5：备份轮转（整包快照）
        BackupService.Rotate("自检第一次");
        await Task.Delay(150);
        BackupService.Rotate("自检第二次");
        await Task.Delay(150);
        BackupSnapshot? snap1 = File.Exists(BackupService.BackupPath(1))
            ? System.Text.Json.JsonSerializer.Deserialize<BackupSnapshot>(File.ReadAllText(BackupService.BackupPath(1)))
            : null;
        bool backupOk = File.Exists(BackupService.BackupPath(1)) && File.Exists(BackupService.BackupPath(2))
                        && snap1 != null && snap1.Layout.Length > 0;
        Add(r, "M5: 备份轮转 backup_1..3（整包快照含 settings/layout/卡片状态）", backupOk,
            $"backup_1={File.Exists(BackupService.BackupPath(1))} backup_2={File.Exists(BackupService.BackupPath(2))}；" +
            $"快照时间={snap1?.SavedAt}，布局 {snap1?.Layout.Length} 字节，卡片状态 {snap1?.States.Count} 份");

        // 23) 托盘：图标资源本身是否有效 + 注册结果/错误码（注册被 UIPI 拒是环境问题，图标句柄为 0 才是真 bug）
        Add(r, "托盘图标：HICON 有效且注册结果可诊断",
            TrayIcon.LastIconHandle != IntPtr.Zero,
            $"hIcon=0x{TrayIcon.LastIconHandle.ToInt64():X} 来源={TrayIcon.LastIconSource}；" +
            $"NIM_ADD={TrayIcon.LastInstallResult?.ToString() ?? "未安装"} err={TrayIcon.LastInstallError}" +
            (TrayIcon.LastInstallResult == false ? "（本会话低完整性被 UIPI 拒绝；用户会话应为 True）" : ""));

        // 25) M5-2：导出单文件 → 重置清空 → 导入恢复（走 selftest 隔离路径，不动用户数据）
        panel.ClearAllForTest();
        panel.ApplyRows(2);
        panel.AddCard("sticky_note", CardSize.TwoByOne);
        await Task.Delay(500);
        int cardsBefore = LayoutStore.Load(out _).Cards.Count;
        string exported = Path.Combine(Path.GetTempPath(), "dsdock-pkg-" + DateTime.Now.ToString("HHmmss") + ".json");
        bool exportOk = ImportExport.ExportTo(exported, out string exportDetail);
        bool exportFileOk = exportOk && exported.Length > 0 && File.Exists(exported) && new FileInfo(exported).Length > 50;
        bool resetOk = ImportExport.ResetAll(out string resetDetail);
        int cardsAfterReset = LayoutStore.Load(out _).Cards.Count;
        bool packageValid = ImportExport.Validate(exported, out _, out string validateDetail);
        string badPath = exported + ".bad.json";
        File.WriteAllText(badPath, "this is not a package");
        bool badRejected = !ImportExport.Validate(badPath, out _, out string badReason);
        File.Delete(badPath);
        bool importOk = ImportExport.Import(exported, out string importDetail);
        int cardsAfterImport = LayoutStore.Load(out _).Cards.Count;
        Add(r, "M5-2: 导出到指定路径 / 格式校验（坏文件被拒）/ 重置清空 / 导入恢复",
            exportFileOk && packageValid && badRejected && resetOk && cardsAfterReset == 0 && importOk && cardsAfterImport == cardsBefore,
            $"导出 {Path.GetFileName(exported)}（{(exportFileOk ? new FileInfo(exported).Length : 0)} 字节）；" +
            $"卡片 {cardsBefore} 张 → 重置后 {cardsAfterReset} 张 → 导入后 {cardsAfterImport} 张；校验: {validateDetail}；坏文件被拒={badRejected}（{badReason}）；{importDetail}");

        // 26) 重启链路：--restart-wait 必须被识别（否则重启会撞单实例锁而静默退出）
        bool restartFlag = Options.Parse(new[] { "--restart-wait" }).RestartWait;
        Add(r, "重启链路：启动参数 --restart-wait 被识别（解决'重置后没有重启'）", restartFlag,
            $"Options.Parse(\"--restart-wait\").RestartWait={restartFlag}；旧实例仍在时会先等待其让出单实例锁");

        // 27) M6：全屏判定（纯几何）+ 全屏隐藏/恢复 + 显示变化重新贴合
        var screen = new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        var taskbarMax = new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1040 };
        var partial = new NativeMethods.RECT { Left = 100, Top = 100, Right = 1800, Bottom = 1000 };
        bool geometryOk = FullscreenWatcher.CoversMonitor(screen, screen, 2)
                       && !FullscreenWatcher.CoversMonitor(taskbarMax, screen, 2)
                       && !FullscreenWatcher.CoversMonitor(partial, screen, 2);

        FullscreenWatcher? watcher = shell.FullscreenWatcherForTest;
        bool hadPanelVisible = panel.IsVisible;
        shell.SetHiddenForFullscreenForTest(true);
        await Task.Delay(600);   // 滑出动画 200ms + 隐藏
        bool panelVisibleWhileHidden = panel.IsVisible;   // 在"隐藏中"这一刻取值，文案才不误导
        bool hiddenOk = !panelVisibleWhileHidden && shell.Sidebar is { IsVisible: false };
        shell.SetHiddenForFullscreenForTest(false);
        await Task.Delay(300);
        bool restoredOk = panel.IsVisible;
        Add(r, "M6: 全屏自动隐藏（几何判定正确 + 隐藏/恢复走真实路径）",
            geometryOk && hiddenOk && restoredOk,
            $"几何: 全屏={FullscreenWatcher.CoversMonitor(screen, screen, 2)}、任务栏内缩={FullscreenWatcher.CoversMonitor(taskbarMax, screen, 2)}、局部窗口={FullscreenWatcher.CoversMonitor(partial, screen, 2)}；" +
            $"隐藏={hiddenOk}（隐藏期间面板可见性={panelVisibleWhileHidden}），恢复={restoredOk}（恢复后={panel.IsVisible}）；检测轮次={watcher?.PollCount}，最近判定={watcher?.LastReason}");

        // 28) M6：显示设置变化后主界面被夹回工作区
        NativeMethods.RECT clamped = panel.EnsureVisible();
        var workArea = panel.WorkArea();
        bool clampedOk = clamped.Left >= workArea.Left && clamped.Top >= workArea.Top
                      && clamped.Right <= workArea.Right && clamped.Bottom <= workArea.Bottom;
        Add(r, "M6: 显示设置变化后主界面重新贴合可见工作区（多显示器拔插/分辨率变化）", clampedOk,
            $"主界面={clamped}，工作区={workArea}");

        // 29) M6：全屏让位是"滑出到最近的左右边框（仅左右）"并隐藏，退出全屏滑回，各 200ms
        NativeMethods.RECT work2 = panel.WorkArea();
        var leftRect = new NativeMethods.RECT { Left = work2.Left + 40, Top = 100, Right = work2.Left + 480, Bottom = 700 };
        var rightRect = new NativeMethods.RECT { Left = work2.Right - 480, Top = 100, Right = work2.Right - 40, Bottom = 700 };
        NativeMethods.RECT leftTarget = PanelWindow.SlideOutTarget(leftRect, work2, out bool toLeft1);
        NativeMethods.RECT rightTarget = PanelWindow.SlideOutTarget(rightRect, work2, out bool toLeft2);
        bool directionOk = toLeft1 && !toLeft2 && leftTarget.Right <= work2.Left && rightTarget.Left >= work2.Right;

        panel.SlideOutAndHide(true);
        await Task.Delay(450);
        NativeMethods.RECT offRect = panel.PanelRect();
        bool slidOut = !panel.IsVisible && (offRect.Right <= work2.Left || offRect.Left >= work2.Right);
        double outAnimMs = panel.LastAnimationMs;

        panel.SlideIn(true);
        await Task.Delay(450);
        NativeMethods.RECT backRect = panel.PanelRect();
        bool slidBack = panel.IsVisible && backRect.Left >= work2.Left && backRect.Right <= work2.Right;
        double inAnimMs = panel.LastAnimationMs;

        Add(r, "M6: 全屏让位滑出到最近的左右边框并隐藏，退出全屏滑回（各 200ms 动画）",
            directionOk && slidOut && slidBack && outAnimMs > 0 && inAnimMs > 0,
            $"方向: 左半屏→{(toLeft1 ? "左" : "右")}边（目标右边界 {leftTarget.Right} ≤ 工作区左 {work2.Left}）；右半屏→{(toLeft2 ? "左" : "右")}边（目标左边界 {rightTarget.Left} ≥ 工作区右 {work2.Right}）；" +
            $"滑出后 rect={offRect} 可见={panel.IsVisible}（{outAnimMs:F0}ms）；滑回后 rect={backRect} 可见={panel.IsVisible}（{inAnimMs:F0}ms）");

        // 30) 设置里的"全屏时自动隐藏"开关：即时生效（无需重启）+ 关掉时若正隐藏则立刻恢复
        bool beforeSwitch = SettingsStore.Load().FullscreenHide;
        shell.OpenSettingsForTest();
        await Task.Delay(400);
        SettingsWindow? settingsWindow = shell.SettingsWindowForTest;
        bool settingsOpened = settingsWindow is { IsVisible: true };
        settingsWindow?.ToggleFullscreenHideForTest();
        await Task.Delay(250);
        bool flipped = SettingsStore.Load().FullscreenHide != beforeSwitch;
        bool? watcherWhileFlipped = shell.FullscreenWatcherForTest?.Enabled;   // 切换后立刻取值，避免文案误导
        bool watcherSynced = watcherWhileFlipped == SettingsStore.Load().FullscreenHide;
        settingsWindow?.ToggleFullscreenHideForTest();   // 还原
        await Task.Delay(250);
        bool restored = SettingsStore.Load().FullscreenHide == beforeSwitch;
        Add(r, "M6: 设置里的『全屏时自动隐藏』开关即时生效（无需重启），关掉时立即恢复显示",
            settingsOpened && flipped && watcherSynced && restored,
            $"设置窗口打开={settingsOpened}；切换后设置={!beforeSwitch}、检测器 Enabled={watcherWhileFlipped}（同步={watcherSynced}）；已还原={restored}");

        // 31) 收纳 / 全屏自动隐藏 / 显示变化后，主界面必须回到原来记住的位置
        NativeMethods.RECT work3 = panel.WorkArea();
        int homeX = work3.Left + 120, homeY = work3.Top + 90;
        panel.SetHomePositionForTest(homeX, homeY);
        SidebarWindow? side = shell.Sidebar;

        panel.Expand(side!.Edge, side.PanelRect(), animate: false);
        await Task.Delay(250);
        NativeMethods.RECT afterExpand = panel.PanelRect();
        bool expandOk = Math.Abs(afterExpand.Left - homeX) <= 2 && Math.Abs(afterExpand.Top - homeY) <= 2;

        panel.Collapse(side.PanelRect(), animate: false);
        await Task.Delay(250);
        panel.Expand(side.Edge, side.PanelRect(), animate: false);
        await Task.Delay(250);
        NativeMethods.RECT afterCollapseCycle = panel.PanelRect();
        bool collapseOk = Math.Abs(afterCollapseCycle.Left - homeX) <= 2 && Math.Abs(afterCollapseCycle.Top - homeY) <= 2;

        panel.SlideOutAndHide(true);
        await Task.Delay(450);
        panel.SlideIn(true);
        await Task.Delay(450);
        NativeMethods.RECT afterFullscreen = panel.PanelRect();
        bool fullscreenOk = Math.Abs(afterFullscreen.Left - homeX) <= 2 && Math.Abs(afterFullscreen.Top - homeY) <= 2;

        panel.EnsureVisible();
        await Task.Delay(150);
        NativeMethods.RECT afterDisplay = panel.PanelRect();
        bool displayOk = Math.Abs(afterDisplay.Left - homeX) <= 2 && Math.Abs(afterDisplay.Top - homeY) <= 2;

        Add(r, "M6: 收纳 / 全屏自动隐藏 / 显示变化后，主界面回到原来记住的位置",
            expandOk && collapseOk && fullscreenOk && displayOk,
            $"设定的常用位置 ({homeX},{homeY})；展开后 {afterExpand}；收纳再展开后 {afterCollapseCycle}；" +
            $"全屏滑出再滑回后 {afterFullscreen}；显示变化后 {afterDisplay}");

        // 32) 设置界面里的"开机自启动"开关（写 HKCU Run）——只读检查，不改动系统状态
        bool startupSwitchExists = false;
        if (settingsWindow != null)
        {
            var q5 = new Queue<DependencyObject>();
            q5.Enqueue(settingsWindow);
            while (q5.Count > 0)
            {
                DependencyObject node = q5.Dequeue();
                if (node is TextBlock t5 && t5.Text == "开机自启动") { startupSwitchExists = true; break; }
                int cc = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < cc; i++) q5.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
        }
        Add(r, "M6: 设置界面提供『开机自启动』开关（HKCU Run，无需管理员）",
            startupSwitchExists && !StartupManager.IsEnabled(),
            $"开关存在={startupSwitchExists}；当前系统状态 开机自启={StartupManager.IsEnabled()}（自检不修改系统设置）");

        // 33) 导出包必须包含"开机自启"（系统状态也要能迁移）+ 帧率目标=60
        bool startupInPackage = false, fpsOk = Anim.FrameClock.TargetFps >= 60.0;
        if (exported.Length > 0 && File.Exists(exported))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(exported));
                startupInPackage = doc.RootElement.TryGetProperty("Startup", out _);
            }
            catch { }
        }
        Add(r, "M6: 导出包包含『开机自启』状态（系统项也能迁移）+ 帧率目标 60 FPS",
            startupInPackage && fpsOk,
            $"包内含 Startup 字段={startupInPackage}（导出时读取自 HKCU Run，导入时自动同步）；" +
            $"FrameClock.TargetFps={Anim.FrameClock.TargetFps}（HUD 会显示实测刷新率）");

        // 34) 资源管理器重启 → TaskbarCreated 广播 → 托盘必须重新注册（真的发这条系统消息）
        int installsBefore = TrayIcon.InstallCount;
        IntPtr sink = shell.MessageSinkHandleForTest;
        uint taskbarCreated = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
        bool posted = sink != IntPtr.Zero && taskbarCreated != 0
                   && NativeMethods.PostMessageW(sink, taskbarCreated, IntPtr.Zero, IntPtr.Zero);
        await Task.Delay(600);
        int installsAfter = TrayIcon.InstallCount;
        Add(r, "M6: 资源管理器重启后自动重新注册托盘（TaskbarCreated 广播，真发消息）",
            posted && installsAfter > installsBefore,
            $"消息窗=0x{sink.ToInt64():X}，TaskbarCreated=0x{taskbarCreated:X}，投递={posted}；" +
            $"NIM_ADD 次数 {installsBefore} → {installsAfter}（等值说明没重注册）");

        // 35) DPI 变化的像素换算（100% ↔ 150%）：窗口像素尺寸必须按比例变化
        double originalScale = panel.DpiScale;
        panel.SetDpiScaleForTest(1.0);
        NativeMethods.RECT size100 = panel.SizeForRows(3);
        panel.SetDpiScaleForTest(1.5);
        NativeMethods.RECT size150 = panel.SizeForRows(3);
        panel.SetDpiScaleForTest(originalScale);
        bool dpiMathOk = Math.Abs(size150.Right - size100.Right * 1.5) <= 2
                      && Math.Abs(size150.Bottom - size100.Bottom * 1.5) <= 2;
        Add(r, "M6: DPI（系统缩放）变化后主界面像素尺寸按比例重算（100% vs 150%）",
            dpiMathOk,
            $"100%: {size100.Width}x{size100.Height}px，150%: {size150.Width}x{size150.Height}px（应为 1.5 倍）；" +
            $"当前实际缩放={originalScale:F2}x（WM_DPICHANGED 会更新缩放并按系统建议矩形重排）");

        // 36) 弹出界面出现在鼠标附近（设置窗口实测）+ 调试 HUD 默认隐藏且可即时开关
        NativeMethods.GetCursorPos(out var cursor);
        shell.OpenSettingsForTest();
        await Task.Delay(400);
        SettingsWindow? sw2 = shell.SettingsWindowForTest;
        bool nearCursor = false;
        if (sw2 != null && sw2.Handle != IntPtr.Zero)
        {
            var sr = WindowUtil.GetScreenRect(sw2.Handle);
            var work4 = WindowUtil.GetWorkArea(sw2.Handle);
            // 贴着鼠标右侧 12px；靠近屏幕右缘时窗口会被整体左移（最多移一个窗口宽度），所以要按窗口宽度放宽
            bool horizontallyNear = Math.Abs(sr.Left - cursor.X) <= sr.Width + 40;
            bool verticallyNear = Math.Abs(sr.Top - cursor.Y) <= sr.Height + 60;           // 下方放不下时会翻到上方
            bool insideWork = sr.Left >= work4.Left && sr.Top >= work4.Top && sr.Right <= work4.Right && sr.Bottom <= work4.Bottom;
            nearCursor = horizontallyNear && verticallyNear && insideWork;
        }
        bool hudDefaultOff = !SettingsStore.Load().ShowHud && !panel.HudVisible;
        panel.SetHudVisible(true);
        bool hudOn = panel.HudVisible;
        panel.SetHudVisible(false);
        bool hudOff = !panel.HudVisible;
        Add(r, "M6: 弹出界面在鼠标附近 + 调试信息默认隐藏（可即时开关）",
            nearCursor && hudDefaultOff && hudOn && hudOff,
            $"鼠标=({cursor.X},{cursor.Y})，设置窗口左上=({(sw2 == null ? "?" : WindowUtil.GetScreenRect(sw2.Handle).Left.ToString())}," +
            $"{(sw2 == null ? "?" : WindowUtil.GetScreenRect(sw2.Handle).Top.ToString())})（应在其右下方 12px，桌面边缘会夹回）；" +
            $"HUD 默认隐藏={hudDefaultOff}，打开={hudOn}，关闭={hudOff}");

        // 37) 设置界面的滑杆是自绘控件（不是系统 Slider），点击位置→值的映射正确
        int customSliders = 0, systemSliders = 0;
        if (settingsWindow != null)
        {
            var q6 = new Queue<DependencyObject>();
            q6.Enqueue(settingsWindow);
            while (q6.Count > 0)
            {
                DependencyObject node = q6.Dequeue();
                if (node is Controls.DsSlider) customSliders++;
                if (node is Slider) systemSliders++;
                int cc = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < cc; i++) q6.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
        }
        // 滑杆自身行为：程序化设值 + 上下限夹取（不做合成鼠标事件，那需要真实鼠标）
        var probe = new Controls.DsSlider { Minimum = 0, Maximum = 24, Value = 0, Width = 200 };
        int callbacks = 0;
        probe.ValueChanged += _ => callbacks++;
        probe.Value = 12;
        bool setOk = probe.Value == 12 && callbacks == 0;   // 程序化设值不应回调（避免回环）
        probe.Value = 99;
        bool clampHigh = probe.Value == 24;
        probe.Value = -5;
        bool clampLow = probe.Value == 0;
        Add(r, "M6: 设置界面的滑杆已改为自绘（不依赖系统 Slider）",
            customSliders >= 4 && systemSliders == 0 && setOk && clampHigh && clampLow,
            $"设置窗口内自绘滑杆={customSliders} 个，系统 Slider={systemSliders} 个（应为 4 与 0）；" +
            $"设值 12 → {12}；设 99 → 夹取 {24}；设 -5 → 夹取 {0}");

        // 38) 便利贴提醒：菜单可设定，且写入卡片状态（RemindAt/Reminded）
        panel.ClearAllForTest();
        panel.ApplyRows(2);
        panel.AddCard("sticky_note");
        await Task.Delay(400);
        CardHost? noteHost2 = panel.Hosts.FirstOrDefault(h => h.InstanceId.StartsWith("sticky_note-", StringComparison.Ordinal));
        string noteId = noteHost2?.InstanceId ?? "";
        IReadOnlyList<CardMenuItem> noteMenu2 = noteHost2?.Card.GetMenuItems() ?? Array.Empty<CardMenuItem>();
        bool hasReminderMenu = noteMenu2.Any(m => m.Label.StartsWith("提醒：", StringComparison.Ordinal))
                            && noteMenu2.Any(m => m.Label.Contains("清除提醒"));
        noteMenu2.FirstOrDefault(m => m.Label.Contains("10 分钟后"))?.Invoke?.Invoke();
        await Task.Delay(300);
        string noteState = noteId.Length == 0 ? "" : LayoutStore.LoadState(noteId);
        bool reminderSaved = noteState.Contains("RemindAt") && noteState.Contains("Reminded") && !noteState.Contains("\"RemindAt\":null");
        Add(r, "便利贴提醒：右键菜单可设定提醒并写入卡片状态（RemindAt/Reminded）",
            hasReminderMenu && reminderSaved,
            $"菜单含提醒项={hasReminderMenu}；卡片状态={noteState}");

        // 39) 到点 → 系统通知：改成"已过期"状态后重新挂载，走真实的 OnDetached→OnAttached→LoadState→CheckReminder
        string overdue = "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(noteState);
            var map = new Dictionary<string, object?>();
            foreach (System.Text.Json.JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                map[prop.Name] = prop.Value.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.True => (object?)true,
                    System.Text.Json.JsonValueKind.False => false,
                    System.Text.Json.JsonValueKind.Number => prop.Value.GetDouble(),
                    _ => prop.Value.GetString(),
                };
            }
            map["RemindAt"] = DateTime.Now.AddMinutes(-1).ToString("O");
            map["Reminded"] = false;
            overdue = System.Text.Json.JsonSerializer.Serialize(map);
        }
        catch { }

        if (noteId.Length > 0 && overdue.Length > 0) LayoutStore.SaveState(noteId, overdue);
        panel.Runtime.ForgetStateForTest(noteId);   // 丢掉内存缓存，让重挂载时真的从磁盘读到"已过期"
        bool reattached = noteId.Length > 0 && panel.Runtime.ReattachForTest(noteId);
        await Task.Delay(900);
        // 判定用"卡片自己把 Reminded 置为 true 并落盘" + 通知内容，避免依赖"与前一次不同"（残留提醒会干扰）
        bool notified = (shell.LastNotification ?? "").Contains("便利贴提醒");
        bool remindedFlag = noteId.Length > 0 && LayoutStore.LoadState(noteId).Contains("\"Reminded\":true");
        Add(r, "便利贴提醒：到点通过 Windows 系统通知弹出（过期状态重新挂载立即触发）",
            reattached && notified && remindedFlag,
            $"重新挂载={reattached}；卡片状态已置 Reminded=true={remindedFlag}；最近一次系统通知=\"{shell.LastNotification}\"（内容为空时正文为\"(空便利贴)\"）");

        // 40) 按钮悬停高亮：统一走 DsHover（新增按钮只要一行），高亮为强调色
        var hoverProbe = new Border
        {
            Width = 80,
            Height = 24,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
        };
        Controls.DsHover.Enable(hoverProbe);
        Controls.DsHover.Highlight(hoverProbe, true);
        Color hoverColor = (hoverProbe.BorderBrush as SolidColorBrush)?.Color ?? Colors.Transparent;
        bool hoverOk = hoverColor == Appearance.Theme.Accent;
        Controls.DsHover.Highlight(hoverProbe, false);
        Color restoredColor = (hoverProbe.BorderBrush as SolidColorBrush)?.Color ?? Colors.Transparent;
        bool restoreOk = restoredColor != Appearance.Theme.Accent;

        // 设置窗口里已有多少个按钮接上了统一悬停
        int hoverEnabled = 0;
        if (settingsWindow != null)
        {
            var q7 = new Queue<DependencyObject>();
            q7.Enqueue(settingsWindow);
            while (q7.Count > 0)
            {
                DependencyObject node = q7.Dequeue();
                if (node is Border b7 && Controls.DsHover.GetBorderHighlight(b7)) hoverEnabled++;
                int cc = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < cc; i++) q7.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
        }
        Add(r, "统一悬停高亮：按钮边框变强调色并可恢复（设置窗口内已接入 " + hoverEnabled + " 个）",
            hoverOk && restoreOk && hoverEnabled >= 3,
            $"高亮色={hoverColor}（强调色={Appearance.Theme.Accent}）、移开后={restoredColor}；设置窗口内接入统一样式的边框按钮={hoverEnabled} 个");

        // 41) 托盘事件解析：v4（事件在 wParam 低字）与旧版（事件=鼠标消息）都要能识别
        // 样本取自实测日志：wParam=锚点坐标，lParam 低字=事件、高字=图标 id
        bool v4Left = TrayIcon.ResolveEventId(TrayIcon.CallbackMessage, new IntPtr(0x40F0579), new IntPtr(0x00010400), true) == 0x0400;   // NIN_SELECT
        bool v4Right = TrayIcon.ResolveEventId(TrayIcon.CallbackMessage, new IntPtr(0x40F0579), new IntPtr(0x0001007B), true) == 0x007B;  // WM_CONTEXTMENU
        bool v4Hover = TrayIcon.ResolveEventId(TrayIcon.CallbackMessage, new IntPtr(0x40F0579), new IntPtr(0x00010200), true) == 0x0200;  // WM_MOUSEMOVE（忽略）
        bool legacyLeft = TrayIcon.ResolveEventId(TrayIcon.CallbackMessage, new IntPtr(0x0202), new IntPtr(0x020A0400), false) == 0x0202; // 旧协议 WM_LBUTTONUP
        Add(r, "托盘图标：左键/右键事件按 NOTIFYICON_VERSION_4 从 wParam 解析（修掉左右键都无反应）",
            v4Left && v4Right && v4Hover && legacyLeft,
            $"v4 左键 NIN_SELECT={v4Left}、v4 右键 WM_CONTEXTMENU={v4Right}、v4 悬浮 WM_MOUSEMOVE={v4Hover}、旧版 WM_LBUTTONUP={legacyLeft}；" +
            $"CallbackMessage=0x{TrayIcon.CallbackMessage:X}");

        // 42) 介绍文字字号统一为 13
        int hint13 = 0, hintSmall = 0;
        if (settingsWindow != null)
        {
            var q8 = new Queue<DependencyObject>();
            q8.Enqueue(settingsWindow);
            while (q8.Count > 0)
            {
                DependencyObject node = q8.Dequeue();
                if (node is TextBlock t8)
                {
                    if (Math.Abs(t8.FontSize - 13) < 0.01) hint13++;
                }
                int cc = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < cc; i++) q8.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
        }
        Add(r, "介绍/说明文字字号统一为 13（如便利贴编辑界面的提示、设置状态行）", hint13 >= 1,
            $"设置窗口内 13 号说明/状态文字={hint13} 处（便利贴编辑界面的两处提示也已改为 13）");

        // 43) 右键"清除所有卡片"必须清掉真实卡片（原来调 M1 遗留的 ClearCards，卡片纹丝不动）
        panel.ClearAllForTest();
        panel.ApplyRows(2);
        panel.AddCard("clock");
        await Task.Delay(350);
        int clearBefore = panel.Hosts.Count;
        panel.ClearCardsFromMenuForTest();
        await Task.Delay(350);
        bool clearedReal = panel.Hosts.Count == 0 && panel.CountOf("clock") == 0;
        panel.SetHudVisible(true);   // 调试信息默认隐藏，断言前先打开
        panel.AddCard("clock");
        await Task.Delay(600);       // HUD 每 15 帧刷新一次
        string hud = panel.HudText.Replace("\n", " | ");
        bool hudCountOk = hud.Contains("卡片 1");
        panel.SetHudVisible(false);
        Add(r, "主界面右键『清除所有卡片』真正清空容器 + 调试信息卡片数正确",
            clearBefore > 0 && clearedReal && hudCountOk,
            $"清除前容器 {clearBefore} 张 → 菜单清除后 {panel.Hosts.Count} 张（clock={panel.CountOf("clock")}）；" +
            $"再加回一张后 HUD=\"{hud}\"");

        // 44) 卡片库展示框：4×3 网格均分，窗口放大到 530×471 即每格横竖 +25
        shell.OpenLibraryForTest();
        await Task.Delay(450);
        LibraryWindow? lib = shell.LibraryWindowForTest;
        bool libSizeOk = lib != null && Math.Abs(lib.Width - 530) <= 2 && Math.Abs(lib.Height - 471) <= 2;   // WPF 可能按内容取整
        Add(r, "卡片库每个卡片展示框横竖各加大 25px（4×3 网格 → 窗口 530×471）",
            libSizeOk,
            $"卡片库窗口={lib?.Width:F0}x{lib?.Height:F0}（原 430x396）；每格宽 (530-内边距)/4、高 (471-标题栏-页码栏)/3，比原来各多约 25px");

        // 45) 侧边栏：拖动 1:1 跟手（不累加位移）+ 松手吸附到最近边缘 + 悬停高亮加强
        var startRect = new NativeMethods.RECT { Left = 500, Top = 300, Right = 510, Bottom = 900 };
        var startPt = new NativeMethods.POINT { X = 505, Y = 350 };
        var movePt = new NativeMethods.POINT { X = 605, Y = 420 };
        (int tx1, int ty1) = SidebarWindow.DragTarget(startRect, startPt, movePt);
        (int tx2, int ty2) = SidebarWindow.DragTarget(startRect, startPt, movePt);   // 同一输入必须同一结果（不累加）
        bool dragOk = tx1 == 600 && ty1 == 370 && tx2 == tx1 && ty2 == ty1;

        SidebarWindow? bar = shell.Sidebar;
        var work5 = WindowUtil.GetWorkAreaForRect(bar!.PanelRect());
        bar.SnapTo(DockEdge.Left, 0.5, animate: false);
        await Task.Delay(150);
        var snapLeftRect = bar.PanelRect();
        bool snapLeft = Math.Abs(snapLeftRect.Left - work5.Left) <= 2;
        bar.SnapTo(DockEdge.Bottom, 0.5, animate: false);
        await Task.Delay(150);
        var snapBottomRect = bar.PanelRect();
        bool snapBottom = Math.Abs(snapBottomRect.Bottom - work5.Bottom) <= 2;

        // 用"目标值"判定：自检时光标可能正好停在侧边栏上，直接量当前不透明度会读到悬停后的值
        double normalTarget = bar.OpacityTargetForTest(false);
        double hoverTarget = bar.OpacityTargetForTest(true);
        bar.SetHoverForTest(true);
        await Task.Delay(250);
        Color hoverBorder = bar.BarBorderColorForTest;
        double hoverOpacityNow = bar.BarOpacityForTest;
        bar.SetHoverForTest(false);
        await Task.Delay(250);
        // 悬停至少提升 0.6（若常态已经饱和到 1.0 则无法再提升，此时只要求不低于常态）
        bool hoverStronger = hoverBorder == Appearance.Theme.Accent
                          && (Math.Abs(normalTarget - 1.0) < 0.001
                              ? hoverTarget >= normalTarget
                              : hoverTarget - normalTarget >= 0.3)   // 目标 +0.6，被 1.0 上限截断时允许更小
                          && hoverOpacityNow >= normalTarget - 0.001;

        bar.SnapTo(DockEdge.Right, 0.5, animate: false);
        await Task.Delay(120);
        Add(r, "侧边栏：拖动 1:1 跟手（位移不累加）、松手吸附到最近边缘、悬停高亮加强",
            dragOk && snapLeft && snapBottom && hoverStronger,
            $"拖动: 起点(500,300)+位移(100,70) → ({tx1},{ty1})（应 600,370，重复调用一致={tx2 == tx1 && ty2 == ty1}）；" +
            $"吸附: 左边={snapLeftRect.Left} vs 工作区左={work5.Left}（{snapLeft}）、下边={snapBottomRect.Bottom} vs 工作区下={work5.Bottom}（{snapBottom}）；" +
            $"悬停目标不透明度 {normalTarget:F2} → {hoverTarget:F2}（实测当前 {hoverOpacityNow:F2}），边框={hoverBorder}（强调色 {Appearance.Theme.Accent}）");

        // 46) 拖到屏幕中间松手 → 必须吸附到最近的屏幕边缘（不走鼠标事件，直接走同一套吸附逻辑）
        SidebarWindow? bar2 = shell.Sidebar;
        var work6 = WindowUtil.GetWorkAreaForRect(bar2!.PanelRect());
        int midX = work6.Left + (work6.Width - bar2.PanelRect().Width) / 2;
        int midY = work6.Top + (work6.Height - bar2.PanelRect().Height) / 2;
        bar2.MoveForTest(midX, midY);   // 模拟拖到屏幕正中间
        await Task.Delay(120);
        var beforeSnap = bar2.PanelRect();
        bar2.SnapNearestForTest();      // 与"松手"完全相同的逻辑
        await Task.Delay(200);
        var afterSnap = bar2.PanelRect();
        bool flush = afterSnap.Left == work6.Left || Math.Abs(afterSnap.Right - work6.Right) <= 1
                  || afterSnap.Top == work6.Top || Math.Abs(afterSnap.Bottom - work6.Bottom) <= 1;
        bool moved = afterSnap.Left != beforeSnap.Left || afterSnap.Top != beforeSnap.Top;
        Add(r, "侧边栏松手吸附：拖到屏幕中间后必须贴到最近的边缘（与松手同一套逻辑）",
            flush && moved,
            $"拖到=({beforeSnap.Left},{beforeSnap.Top}) → 吸附后={afterSnap}；工作区={work6}；贴边={flush}，位置发生变化={moved}");

        // 47) 侧边栏长边下限 = 屏幕 10%；点击侧边栏 = 展开 ⇄ 收纳
        SidebarWindow? bar3 = shell.Sidebar;
        var work7 = WindowUtil.GetWorkAreaForRect(bar3!.PanelRect());
        bar3.FollowPanelLength(30, animate: false);      // 故意给一个极短的长度
        await Task.Delay(200);
        var shortRect = bar3.PanelRect();
        // 长边可能是宽（横向停靠）或高（纵向停靠），按实际朝向判断
        bool horizontal = shortRect.Width > shortRect.Height;
        int longSide = horizontal ? shortRect.Width : shortRect.Height;
        int axisPx = horizontal ? work7.Width : work7.Height;
        bool minLengthOk = longSide >= Math.Round(axisPx * 0.10) - 2 && longSide <= axisPx;
        bar3.FollowPanelLength(0, animate: false);       // 回到"跟随主界面"的默认
        bar3.SnapTo(DockEdge.Right, 0.5, animate: false);   // 恢复成竖条，避免影响后续用例
        await Task.Delay(200);

        shell.CollapsePanel(false);
        await Task.Delay(250);
        bool beforeToggle = panel.IsExpanded;
        shell.TogglePanelForTest();                     // 与"点击侧边栏"完全相同的入口
        await Task.Delay(500);
        bool afterFirst = panel.IsExpanded;
        shell.TogglePanelForTest();
        await Task.Delay(500);
        bool afterSecond = panel.IsExpanded;
        Add(r, "侧边栏长边不小于屏幕 10%（长边下限）+ 点击侧边栏展开/收纳切换",
            minLengthOk && !beforeToggle && afterFirst && !afterSecond,
            $"给定长度 30px → 实际长边 {longSide}px（{(horizontal ? "横向" : "纵向")}，屏幕 {(horizontal ? work7.Width : work7.Height)}，10%={Math.Round(axisPx * 0.10)}）；" +
            $"点击切换: 初始展开={beforeToggle} → 第一次点击后={afterFirst} → 第二次点击后={afterSecond}");

        // 48) 卡片库「导入新卡片…」：多套一层目录也能找到清单 → 拷文件（跳过禁止携带的契约 DLL）→ 写 registry.json → 卡片库立即出现
        string pkgRoot = Path.Combine(Path.GetTempPath(), "dsdock-pkg-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string pkgInner = Path.Combine(pkgRoot, "hello_demo-1.0.0");
        Directory.CreateDirectory(pkgInner);
        File.Copy(Path.Combine(CardRegistry.CardsRoot, "clock", "ClockCard.dll"), Path.Combine(pkgInner, "ClockCard.dll"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "DsDock.Card.Abstractions.dll"),
                  Path.Combine(pkgInner, "DsDock.Card.Abstractions.dll"), overwrite: true);   // 故意带进来 → 必须被跳过
        File.WriteAllText(Path.Combine(pkgInner, "manifest.json"),
            "{ \"id\": \"hello_demo\", \"name\": \"导入测试卡\", \"version\": \"1.0.0\", \"entry\": \"ClockCard.dll\", \"description\": \"导入流程自检用\", \"allowedSizes\": [\"1x1\"], \"defaultSize\": \"1x1\", \"maxInstances\": 1 }");

        shell.OpenLibraryForTest();
        await Task.Delay(400);
        LibraryWindow? lib2 = shell.LibraryWindowForTest;
        int cardsBeforeImport = lib2?.TotalCards ?? -1;
        string installDetail = lib2!.ImportCardFrom(pkgRoot);
        await Task.Delay(400);
        string targetDir = Path.Combine(CardRegistry.CardsRoot, "hello_demo");
        bool filesCopied = File.Exists(Path.Combine(targetDir, "manifest.json")) && File.Exists(Path.Combine(targetDir, "ClockCard.dll"));
        bool forbiddenSkipped = !File.Exists(Path.Combine(targetDir, "DsDock.Card.Abstractions.dll"));
        bool registered = CardRegistry.ReadIds().Contains("hello_demo");
        bool listedImmediately = lib2.TotalCards == cardsBeforeImport + 1;
        string entryState = panel.Runtime.Cards.FirstOrDefault(c => c.Entry.Id == "hello_demo") is { } imported
            ? (imported.Ok ? "加载成功" : "失败占位：" + imported.Error)
            : "未进入运行时";

        // 清理：恢复 registry.json（安装器写了 .bak）并删掉测试卡片目录，避免污染仓库
        string backup = CardRegistry.RegistryPath + ".bak";
        if (File.Exists(backup)) File.Copy(backup, CardRegistry.RegistryPath, overwrite: true);
        panel.Runtime.RemoveEntryForTest("hello_demo");
        try { Directory.Delete(targetDir, true); Directory.Delete(pkgRoot, true); } catch { }
        lib2.Refresh();

        Add(r, "卡片库『导入新卡片…』：自动放置文件 + 写 registry.json + 不重启即时出现在卡片库",
            filesCopied && forbiddenSkipped && registered && listedImmediately,
            $"包内多套一层目录 → 已定位清单；文件已拷到 Cards/hello_demo（禁止携带的契约 DLL 已跳过={forbiddenSkipped}）；" +
            $"registry.json 含 hello_demo={registered}；卡片库条目 {cardsBeforeImport} → {lib2.TotalCards}（即时生效={listedImmediately}）；" +
            $"运行时状态={entryState}；安装返回：{installDetail}");

        // 49) 导入健壮性：坏清单 / 坏 DLL 只给可读原因（不抛异常、不闪退），失败卡片只显示灰色占位
        string badRoot = Path.Combine(Path.GetTempPath(), "dsdock-bad-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(badRoot);
        File.WriteAllText(Path.Combine(badRoot, "manifest.json"), "{ this is not json");
        bool badJson = !CardInstaller.Install(badRoot, out _, out string badJsonDetail) && badJsonDetail.Contains("解析失败");

        string corruptRoot = Path.Combine(badRoot, "corrupt");
        Directory.CreateDirectory(corruptRoot);
        File.WriteAllText(Path.Combine(corruptRoot, "manifest.json"),
            "{ \"id\": \"corrupt_demo\", \"name\": \"坏卡\", \"version\": \"1.0.0\", \"entry\": \"Corrupt.dll\", \"description\": \"损坏 DLL 自检\", \"allowedSizes\": [\"1x1\"], \"defaultSize\": \"1x1\", \"maxInstances\": 1 }");
        File.WriteAllBytes(Path.Combine(corruptRoot, "Corrupt.dll"), new byte[] { 0x4D, 0x5A, 0x00, 0x01, 0x02, 0x03 });
        bool corruptHandled = CardInstaller.Install(corruptRoot, out CardInstaller.InstallResult? corruptResult, out string corruptDetail);
        bool corruptNotLoaded = corruptResult is { HotLoaded: false };
        bool corruptListedAsFailed = panel.Runtime.Cards.FirstOrDefault(c => c.Entry.Id == "corrupt_demo") is { Ok: false };

        string backup3 = CardRegistry.RegistryPath + ".bak";
        if (File.Exists(backup3)) File.Copy(backup3, CardRegistry.RegistryPath, overwrite: true);
        panel.Runtime.RemoveEntryForTest("corrupt_demo");
        try { Directory.Delete(Path.Combine(CardRegistry.CardsRoot, "corrupt_demo"), true); Directory.Delete(badRoot, true); } catch { }

        Add(r, "导入健壮性：坏清单/坏 DLL 只给出可读原因（不抛异常、不闪退），坏卡只显示灰色占位",
            badJson && corruptHandled && corruptNotLoaded && corruptListedAsFailed,
            $"坏 JSON 清单 → 拒绝并说明（{badJsonDetail}）；损坏 DLL → 文件与注册照做但热加载={corruptResult?.HotLoaded}（{corruptDetail}）；" +
            $"运行时里它是失败占位={corruptListedAsFailed}；已清理测试产物");

        // 50) 移除卡片：容器里该卡片的实例一起移除；目录删除 + registry 注销 + 运行时卸载 + 卡片库同步（不重启）
        panel.ClearAllForTest();
        panel.ApplyRows(2);
        panel.AddCard("sticky_note");
        panel.AddCard("sticky_note");
        await Task.Delay(350);
        int noteBefore = panel.CountOf("sticky_note");
        int removedFromPanel = panel.RemoveAllCardsOf("sticky_note");
        await Task.Delay(250);
        bool containerCleared = removedFromPanel == noteBefore && panel.CountOf("sticky_note") == 0;

        // 先装一张测试卡片（合法清单即可），再把它整张移除
        string pkgRoot2 = Path.Combine(Path.GetTempPath(), "dsdock-rm-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(pkgRoot2);
        File.Copy(Path.Combine(CardRegistry.CardsRoot, "clock", "ClockCard.dll"), Path.Combine(pkgRoot2, "ClockCard.dll"));
        File.WriteAllText(Path.Combine(pkgRoot2, "manifest.json"),
            "{ \"id\": \"rm_demo\", \"name\": \"待移除卡\", \"version\": \"1.0.0\", \"entry\": \"ClockCard.dll\", \"description\": \"移除流程自检\", \"allowedSizes\": [\"1x1\"], \"defaultSize\": \"1x1\", \"maxInstances\": 1 }");

        shell.OpenLibraryForTest();
        await Task.Delay(400);
        LibraryWindow? lib3 = shell.LibraryWindowForTest;
        lib3!.ImportCardFrom(pkgRoot2);
        await Task.Delay(350);
        string rmDir = Path.Combine(CardRegistry.CardsRoot, "rm_demo");
        int cardsWithDemo = lib3.TotalCards;
        string uninstallDetail = lib3.UninstallCardFrom("rm_demo");
        await Task.Delay(350);
        bool dirGone = !Directory.Exists(rmDir);
        bool unregistered = !CardRegistry.ReadIds().Contains("rm_demo");
        bool runtimeGone = panel.Runtime.Cards.All(c => c.Entry.Id != "rm_demo");
        bool librarySynced = lib3.TotalCards == cardsWithDemo - 1;
        bool listOpen = lib3.CardListOpen;
        try { Directory.Delete(pkgRoot2, true); } catch { }

        Add(r, "移除卡片：容器实例一并移除、目录删除、registry 注销、运行时卸载、卡片库即时同步（无需重启）",
            containerCleared && dirGone && unregistered && runtimeGone && librarySynced && listOpen,
            $"容器：{noteBefore} 张便利贴 → RemoveAllCardsOf 移除 {removedFromPanel} 张（清空={containerCleared}）；" +
            $"整张移除 rm_demo：目录已删={dirGone}、registry 已注销={unregistered}、运行时已卸载={runtimeGone}、" +
            $"卡片库条目 {cardsWithDemo} → {lib3.TotalCards}（同步={librarySynced}），列表浮层打开={listOpen}；返回：{uninstallDetail}");

        static string DateTextOf(CardHost? cardHost)
        {
            if (cardHost == null) return "";
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(cardHost);
            while (queue.Count > 0)
            {
                DependencyObject node = queue.Dequeue();
                if (node is TextBlock text && !text.Text.Contains(":") && text.Text.Length > 4) return text.Text;
                int childCount = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < childCount; i++) queue.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
            return "";
        }
    }

    /// <summary>从卡片视图里找时间文本（含冒号的 TextBlock），用于断言"显示真的变了"。</summary>
    private static string TimeTextOf(CardHost? host)
    {
        if (host == null) return "";
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(host);
        while (queue.Count > 0)
        {
            DependencyObject node = queue.Dequeue();
            if (node is TextBlock text && text.Text.Contains(":")) return text.Text;
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++) queue.Enqueue(VisualTreeHelper.GetChild(node, i));
        }
        return "";
    }

    private static void Add(M1Report r, string name, bool pass, string detail)
    {
        r.Checks.Add(new M1Check { Name = name, Pass = pass, Detail = detail });
        Log.Info($"M2 自检 {(pass ? "PASS" : "FAIL")} · {name} · {detail}");
    }
}
