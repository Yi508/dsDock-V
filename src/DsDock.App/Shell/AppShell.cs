using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using DsDock.Anim;
using DsDock.Appearance;
using DsDock.Card.Abstractions;
using DsDock.Controls;
using DsDock.Diagnostics;
using DsDock.Platform;
using DsDock.Plugins;
using DsDock.Storage;
using DsDock.Windows;

namespace DsDock.Shell;

/// <summary>生命周期编排：消息窗、托盘、侧边栏、主界面、设置窗口，以及退出。</summary>
internal sealed class AppShell
{
    private readonly Options _options;
    private readonly Application _app;
    private readonly Stopwatch _watch;
    private readonly SettingsStore _settings;

    private MessageSink? _messages;
    private TrayIcon? _tray;
    private FrameClock? _clock;
    private WindowAnimator? _animator;
    private SidebarWindow? _sidebar;
    private PanelWindow? _panel;
    private SettingsWindow? _settingsWindow;
    private LibraryWindow? _libraryWindow;
    private DsMenu? _menu;
    private FullscreenWatcher? _fullscreen;
    private bool _panelWasExpanded;
    private bool _exiting;

    public AppShell(Options options, Application app, Stopwatch watch, SettingsStore settings)
    {
        _options = options;
        _app = app;
        _watch = watch;
        _settings = settings;
    }

    public int ExitCode { get; private set; }
    public double StartupMs => _watch.Elapsed.TotalMilliseconds;
    public bool SuppressExitForTest { get; set; }
    public int MenuCount { get; private set; }
    public DsMenu? LastMenu => _menu;
    public bool ExitRequestedForTest { get; private set; }

    public PanelWindow? Panel => _panel;
    public SidebarWindow? Sidebar => _sidebar;

    public void Start()
    {
        _messages = new MessageSink();
        _messages.Activated += () => Dispatch(() => ExpandPanel(true));
        _messages.TaskbarCreated += () => Dispatch(ReinstallTray);
        _messages.TrayMessage += (msg, wParam, lParam) =>
        {
            uint evt = TrayIcon.ResolveEventId(msg, wParam, lParam);
            // 只记点击/右键类事件，鼠标移动与悬浮提示不刷屏
            if (evt is 0x0202 or 0x0205 or 0x0400 or 0x0401 or 0x007B)
                Log.Info($"托盘回调: msg=0x{msg:X} wParam=0x{wParam.ToInt64():X} lParam=0x{lParam.ToInt64():X} → 事件=0x{evt:X}");
            Dispatch(() => OnTrayMessage(evt));
        };
        InstallTray();   // 提前安装：卡片加载失败的气泡要在加载前就可用

        _clock = new FrameClock();
        _clock.Start();
        _animator = new WindowAnimator(_clock);

        _sidebar = new SidebarWindow(_options, _settings, _clock);
        _sidebar.ExpandRequested += () => Dispatch(TogglePanel);   // 点一下展开，再点一下收纳
        _sidebar.Start();

        _panel = new PanelWindow(_options, _settings, _clock, _animator);
        _panel.SettingsRequested += OpenSettings;
        _panel.LibraryRequested += ShowLibraryNotice;
        _panel.CollapseRequested += () => CollapsePanel(true);
        _panel.ExitRequested += RequestExit;
        _panel.Start();
        _panel.SetHudVisible(_settings.ShowHud);   // 默认隐藏调试信息
        ImportExport.StartupStateProvider = StartupManager.IsEnabled;   // 导出时带上开机自启状态
        // 「导入新卡片」：热加载器指向运行时；安装完成后刷新卡片库（无需重启）
        // 覆盖安装前：把同 id 的卡片从容器摘掉并卸载程序集（否则 DLL 被锁，拷贝会失败）
        CardInstaller.PreInstallUnload = id =>
        {
            int removed = _panel?.RemoveAllCardsOf(id) ?? 0;
            bool unloaded = _panel?.Runtime.UnloadCard(id) ?? false;
            Log.Info($"覆盖安装前准备 {id}: 容器移除 {removed} 张，程序集卸载={unloaded}");
            return true;
        };
        CardInstaller.HotLoader = id =>
        {
            bool hotOk = _panel!.Runtime.InstallHot(id, out string hotDetail);
            Dispatch(() => _libraryWindow?.Refresh());
            return (hotOk, hotDetail);
        };
        _panel.Runtime.NotifyRequested = (title, text) =>
        {
            LastNotification = title + " / " + text;
            bool sent = _tray?.ShowBalloon(title, text) ?? false;   // 卡片定时器在 UI 线程上，直接发
            Log.Info($"系统通知（卡片请求）: {LastNotification} → {(sent ? "已发送" : "未发送/托盘不可用")}");
            return sent;
        };
        _panel.Runtime.LoadFailureNotified = (title, text) => Dispatch(() => _tray?.ShowBalloon(title, text));
        _panel.Runtime.Load();

        // M5：先按"每日一次"备份，再从 layout.json 恢复容器（损坏则整包回退）
        bool firstRun = !System.IO.File.Exists(Storage.LayoutStore.LayoutPath);
        Log.Info("备份: " + BackupService.RotateIfNeeded());
        Log.Info("布局恢复: " + _panel.RestoreFromLayout());
        if (firstRun && _panel.HostCount == 0) _panel.AddCard("clock");

        // 侧边栏长度跟随主界面（尺寸挡位变化或停靠边变化时都重新同步）
        _panel.RowsChanged += SyncSidebarLength;
        _sidebar.EdgeChanged += SyncSidebarLength;
        SyncSidebarLength();

        _clock.OnTick += () => _panel?.UpdateHud();

        // M6：全屏应用/游戏运行时自动让位；显示设置变化后重新贴合工作区
        // 兜底：任何未处理异常只记录日志，不让应用闪退（导入坏卡片等场景）
        Application.Current.DispatcherUnhandledException += (_, e) =>
        {
            Log.Info($"未处理异常（已阻止崩溃）: {e.Exception}");
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Info($"致命异常（进程即将退出）: {e.ExceptionObject}");

        // 首轮异常日志：即使异常随后被捕获，也能在日志里看到类型与消息（用于定位偶发闪退）
        int firstChance = 0;
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (System.Threading.Interlocked.Increment(ref firstChance) <= 40)
                Log.Info($"首轮异常: {e.Exception.GetType().Name} — {e.Exception.Message}");
        };

        _panel.DisplayChanged += () => Dispatch(OnDisplayChanged);
        _panel.DpiChanged += () => Dispatch(() => { _sidebar?.ApplyGeometry(false); SyncSidebarLength(); });
        _fullscreen = new FullscreenWatcher(TimeSpan.FromMilliseconds(500),
            () => new[] { _panel.Handle, _sidebar.Handle, _settingsWindow?.Handle ?? IntPtr.Zero })
        {
            Enabled = _settings.FullscreenHide,
        };
        _fullscreen.FullscreenChanged += hidden => Dispatch(() => SetHiddenForFullscreen(hidden));
        _fullscreen.Start();
        Log.Info($"全屏自动隐藏: {(_settings.FullscreenHide ? "开启" : "关闭")}");

        // 已确认：启动自动展开主界面（文档原文是"只显示侧边栏"，此处按你的决定偏离）
        if (_options.AutoExpand) ExpandPanel(animate: false);
        else CollapsePanel(animate: false);

        InstallTray();
        Log.Info($"启动完成: 侧边栏={_sidebar.PanelRect()} 主界面={_panel.PanelRect()} 展开={_panel.IsExpanded}");

        if (_options.SelfTestPath != null)
            _panel.Dispatcher.BeginInvoke(new Action(() => { _ = RunSelfTestAsync(); }));
    }

    private void Dispatch(Action action)
    {
        if (_panel == null || _panel.Dispatcher.HasShutdownStarted) return;
        _panel.Dispatcher.BeginInvoke(action);
    }

    /// <summary>侧边栏长度 = 主界面在停靠方向上的长度。</summary>
    private void SyncSidebarLength()
    {
        if (_panel == null || _sidebar == null) return;
        var rect = _panel.PanelRect();
        if (rect.Width <= 0) return;
        int length = _sidebar.Edge.IsVertical() ? rect.Height : rect.Width;
        _sidebar.FollowPanelLength(length);
        Log.Info($"侧边栏长度跟随主界面: 边={_sidebar.Edge} 长度={length}px");
    }

    /// <summary>进入/退出全屏时隐藏或恢复侧边栏与主界面。</summary>
    private void SetHiddenForFullscreen(bool hidden)
    {
        if (hidden)
        {
            _panelWasExpanded = _panel?.IsExpanded == true;

            // 滑出到最近的左右边框（200ms）再隐藏；侧边栏是细条，直接隐藏
            if (_panelWasExpanded) _panel?.SlideOutAndHide(true);
            else _panel?.SetVisible(false);
            _sidebar?.SetVisible(false);

            Log.Info("全屏让位：主界面滑出到最近的左右边框（200ms），退出全屏后滑回");
            return;
        }

        _sidebar?.SetVisible(true);
        if (_panelWasExpanded) _panel?.SlideIn(true);   // 从屏幕外滑回原位（200ms）
        Log.Info($"已退出全屏并恢复（主界面展开={_panelWasExpanded}，滑回动画）");
    }

    /// <summary>显示器拔插 / 分辨率或 DPI 变化：把窗口夹回可见工作区并重算侧边栏长度。</summary>
    private void OnDisplayChanged()
    {
        _panel?.EnsureVisible();
        _sidebar?.ApplyGeometry(false);
        SyncSidebarLength();
        Log.Info("显示变化处理完成: " + (_panel == null ? "(无面板)" : _panel.PanelRect().ToString()));
    }

    /// <summary>设置里的"全屏时自动隐藏"开关：即时生效，不需要重启。</summary>
    private void ApplyFullscreenHideSetting(bool enabled)
    {
        _settings.FullscreenHide = enabled;
        _settings.Save();

        if (_fullscreen != null)
        {
            _fullscreen.Enabled = enabled;
            // 刚关掉开关时如果正处在"已隐藏"状态，立刻恢复显示
            if (!enabled && _fullscreen.IsHidden) _fullscreen.Apply(false, "开关已关闭，立即恢复显示");
        }

        Log.Info($"全屏时自动隐藏已{(enabled ? "开启" : "关闭")}（即时生效，不需要重启）");
    }

    public IntPtr MessageSinkHandleForTest => _messages?.Handle ?? IntPtr.Zero;

    /// <summary>最近一次系统通知（自检断言用）。</summary>
    public string? LastNotification { get; private set; }
    public void OpenSettingsForTest() => OpenSettings();
    public SettingsWindow? SettingsWindowForTest => _settingsWindow;
    public FullscreenWatcher? FullscreenWatcherForTest => _fullscreen;
    public void SetHiddenForFullscreenForTest(bool hidden) => SetHiddenForFullscreen(hidden);

    // ---------------------------------------------------------------- 主界面 展开/收回

    public void TogglePanelForTest() => TogglePanel();   // 与点击侧边栏共用同一入口


    public void ExpandPanel(bool animate)
    {
        if (_panel == null || _sidebar == null) return;
        _sidebar.SetVisible(true);
        _panel.Expand(_sidebar.Edge, _sidebar.PanelRect(), animate);
    }

    public void CollapsePanel(bool animate)
    {
        if (_panel == null || _sidebar == null) return;
        _panel.Collapse(_sidebar.PanelRect(), animate);
    }

    public void TogglePanel()
    {
        if (_panel?.IsExpanded == true) CollapsePanel(true);
        else ExpandPanel(true);
    }

    // ---------------------------------------------------------------- 设置 / 卡片库

    /// <summary>弹出窗口的默认位置：鼠标附近（右下 12px），ShowAt/ShowNear 会夹回工作区。</summary>
    private static (int X, int Y) CursorAnchor()
    {
        NativeMethods.GetCursorPos(out var pt);
        return (pt.X + 12, pt.Y + 12);
    }

    /// <summary>卡片库「导入新卡片…」：安装（拷文件 + 写 registry.json + 热加载）并刷新卡片库。</summary>
    private string InstallCardFromFolder(string folder)
    {
        try
        {
        bool ok = CardInstaller.Install(folder, out CardInstaller.InstallResult? result, out string detail);
        if (ok && result != null)
        {
            Dispatch(() => _libraryWindow?.Refresh());
            Log.Info($"导入新卡片成功: {result.Id} v{result.Version} → {result.TargetDir}（{result.FileCount} 个文件；热加载={result.HotLoaded}）");
        }
        return detail;
        }
        catch (Exception ex)
        {
            string detail = $"安装失败（已阻止崩溃）: {ex.GetType().Name} — {ex.Message}";
            Log.Info(detail);
            return detail;
        }
    }

    /// <summary>
    /// 弹文件夹选择框：现代 IFileOpenDialog（与导入配置的打开文件对话框同款外观），
    /// 并挂到卡片库窗口上 —— 有 owner 的对话框天然在上面，不靠临时取消置顶。
    /// </summary>
    private string? PickFolder()
    {
        Log.Info("导入新卡片: 打开文件夹选择器");
        LibraryWindow? lib = _libraryWindow;
        string? folder = FolderPicker.Pick(lib != null ? lib : _panel, "选择卡片文件夹（内含 manifest.json 与卡片 DLL）");
        Log.Info($"导入新卡片: 选择结果 = {(string.IsNullOrEmpty(folder) ? "(取消)" : folder)}");
        return folder;
    }

    /// <summary>移除整张卡片：先摘掉容器里的实例与状态文件，再删目录 + 注销 registry，最后卸掉运行时账目。</summary>
    private string UninstallCard(string id)
    {
        try
        {
            IReadOnlyList<string> instances = _panel?.Runtime.InstancesOf(id) ?? Array.Empty<string>();
            int removedFromPanel = _panel?.RemoveAllCardsOf(id) ?? 0;
            foreach (string instanceId in instances) LayoutStore.DeleteState(instanceId);

            // 顺序很重要：先卸载程序集（释放 DLL 句柄），再删目录/注销注册表
            _panel?.Runtime.UnloadCard(id);
            bool ok = CardInstaller.Uninstall(id, out string detail);
            _panel?.SaveLayout();

            detail = $"已移除卡片 {id}（容器内移除 {removedFromPanel} 张" +
                     (instances.Count > 0 ? $"，清理 {instances.Count} 份状态" : "") + "）；" + detail;
            if (ok) Dispatch(() => _libraryWindow?.Refresh());
            Log.Info(detail);
            return detail;
        }
        catch (Exception ex)
        {
            string detail = $"移除失败（已阻止崩溃）: {ex.GetType().Name} — {ex.Message}";
            Log.Info(detail);
            return detail;
        }
    }

    private void OpenSettings()
    {
        if (_libraryWindow is { IsVisible: true })
        {
            _libraryWindow.Close();
            _libraryWindow = null;
        }
        if (_settingsWindow is { IsVisible: true })
        {
            Log.Info("设置窗口已打开");
            return;
        }

        var window = new SettingsWindow(_panel!, ApplyFullscreenHideSetting);

        // 按要求：弹出界面出现在鼠标附近（拖动后的位置仍会记在 settings.json 里备用）
        (int cursorX, int cursorY) = CursorAnchor();
        window.ShowAt(cursorX, cursorY);
        window.Closing += (_, _) =>
        {
            var rect = WindowUtil.GetScreenRect(window.Handle);
            if (rect.Width > 0)
            {
                _settings.SettingsLeft = rect.Left;
                _settings.SettingsTop = rect.Top;
            }
            SyncAppearanceTo(_settings);
            _settings.Save();
            _settingsWindow = null;
        };
        _settingsWindow = window;
        Log.Info("设置窗口已打开");
    }

    /// <summary>卡片库按钮 / 托盘菜单 / 自检都走这里。与设置界面互斥（spec）。</summary>
    private void ShowLibraryNotice()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Close();
            _settingsWindow = null;
        }
        if (_libraryWindow is { IsVisible: true })
        {
            Log.Info("卡片库已打开");
            return;
        }

        var window = new LibraryWindow(_panel!.Runtime,
            id => _panel.AddCard(id),
            id => _panel.RemoveCard(id),
            InstallCardFromFolder,
            PickFolder,
            UninstallCard);
        (int cursorX, int cursorY) = CursorAnchor();
        window.ShowAt(cursorX, cursorY);
        _libraryWindow = window;
        Log.Info("卡片库已打开");
    }

    public void OpenLibraryForTest() => ShowLibraryNotice();

    public string? LastBalloonText => _tray?.LastBalloonText;
    public bool? LastBalloonResult => _tray?.LastBalloonResult;
    public void NotifyCardFailureForTest(string cardId, string error)
        => _tray?.ShowBalloon("卡片加载失败", $"{cardId}：{error}（已跳过，其他卡片照常）");
    public LibraryWindow? LibraryWindowForTest => _libraryWindow;
    public void CloseLibraryForTest()
    {
        _libraryWindow?.Close();
        _libraryWindow = null;
    }

    /// <summary>供自检复用：把当前外观写入 settings.json（与关闭设置窗口时相同的路径）。</summary>
    public void PersistForTest()
    {
        SyncAppearanceTo(_settings);
        _settings.Save();
    }

    private void SyncAppearanceTo(SettingsStore settings)
    {
        settings.Alpha = Theme.Alpha;
        settings.Frost = Theme.Frost;
        settings.Corner = Theme.Corner;
        settings.FontSize = Theme.FontSize;
        settings.Accent = Theme.ToHex(Theme.Accent);
        settings.Rows = _panel?.Rows ?? settings.Rows;
        settings.Locked = _panel?.Locked ?? settings.Locked;
    }

    // ---------------------------------------------------------------- 托盘

    private int _trayAttempts;

    private void InstallTray()
    {
        if (_options.NoTray || _messages == null) return;
        if (_tray != null && TrayIcon.LastInstallResult == true) return;

        if (_messages.Handle == IntPtr.Zero)
        {
            Log.Info("托盘安装推迟：消息窗句柄尚未就绪");
            ScheduleTrayRetry();
            return;
        }
        if (_tray != null)
        {
            _tray.Dispose();
            _tray = null;
        }

        _trayAttempts++;
        _tray = new TrayIcon();
        _tray.Install(_messages.Handle, StatusTip());

        if (TrayIcon.LastInstallResult == true)
        {
            // 装好了：Windows 11 默认会把新的托盘图标折叠进 "^" 溢出区，提醒一次免得以为没生效
            Dispatch(() => _tray?.ShowBalloon("桌面备忘录已在运行",
                "如果任务栏上看不到图标，请点开任务栏的 ^ 溢出区，把本图标拖到任务栏（或右键任务栏 → 任务栏设置 → 其他系统托盘图标）。"));
            return;
        }

        if (TrayIcon.LastInstallError == 5)
        {
            // ERROR_ACCESS_DENIED：可能是权限不够，也可能只是 explorer 的托盘尚未就绪 → 仍重试几次
            Log.Info($"托盘安装被拒（err=5，进程完整性={ProcessIntegrity.Describe()}，父进程={ProcessIntegrity.DescribeParent()}）");
            Dispatch(() => _panel?.ShowPanelHint("托盘图标注册被系统拒绝，正在重试（详见日志：进程完整性/父进程）"));
            ScheduleTrayRetry();
            return;
        }

        Log.Info($"托盘第 {_trayAttempts} 次安装未成功（err={TrayIcon.LastInstallError}），稍后重试");
        ScheduleTrayRetry();
    }

    /// <summary>句柄未就绪或安装失败时的兜底重试（最多 4 次，间隔 2s）。</summary>
    private void ScheduleTrayRetry()
    {
        if (_trayAttempts >= 4 || _panel == null) return;
        _panel.Dispatcher.BeginInvoke(new Action(async () =>
        {
            await Task.Delay(2000);
            InstallTray();
        }));
    }

    private string StatusTip() => _panel?.IsExpanded == true ? "桌面备忘录 - 已显示" : "桌面备忘录 - 已隐藏";

    private void ReinstallTray()
    {
        Log.Info("TaskbarCreated：重新注册托盘图标");
        _tray?.Dispose();
        _tray = null;
        InstallTray();
    }

    private void OnTrayMessage(uint msg)
    {
        const uint NIN_SELECT = 0x0400;          // NOTIFYICON_VERSION_4 左键
        const uint NIN_KEYSELECT = 0x0401;
        if (msg == NativeMethods.WM_LBUTTONUP || msg == NIN_SELECT || msg == NIN_KEYSELECT)
        {
            // spec：点击托盘图标展开主界面
            Log.Info("托盘左键：展开主界面");
            ExpandPanel(true);
            _tray?.UpdateTip(StatusTip());
        }
        else if (msg == NativeMethods.WM_RBUTTONUP || msg == NativeMethods.WM_CONTEXTMENU)
        {
            Log.Info("托盘右键：自绘菜单");
            NativeMethods.GetCursorPos(out var pt);
            _menu?.CloseMenu();

            var menu = new DsMenu(Theme.FontSize, Theme.Accent, (int)Theme.TokenDuration("MenuFadeMs", 100));
            menu.AddItem(_panel?.IsExpanded == true ? "收起主界面" : "显示主界面", () => { TogglePanel(); _tray?.UpdateTip(StatusTip()); });
            menu.AddItem("设置", OpenSettings);
            menu.AddItem("卡片库", ShowLibraryNotice);
            menu.AddSeparator();
            menu.AddItem(StartupManager.IsEnabled() ? "开机自启（已开启，点击关闭）" : "开机自启（已关闭，点击开启）", ToggleStartup);
            menu.AddSeparator();
            menu.AddItem("退出应用", RequestExit);
            menu.ShowAt(pt.X, pt.Y);

            _menu = menu;
            MenuCount++;
            Log.Info($"托盘菜单已显示（第 {MenuCount} 次）: {string.Join(" | ", menu.ItemLabels)}");
        }
    }

    /// <summary>供自检复用：与托盘右键完全相同的菜单路径。</summary>
    public void ShowTrayMenu()
    {
        NativeMethods.GetCursorPos(out var pt);
        OnTrayMessage(NativeMethods.WM_RBUTTONUP);
        Log.Info($"ShowTrayMenu at ({pt.X},{pt.Y})");
    }

    private void ShowMenuAt(int x, int y, string label, Action? action, bool enabled)
    {
        _menu?.CloseMenu();
        var menu = new DsMenu(Theme.FontSize, Theme.Accent, (int)Theme.TokenDuration("MenuFadeMs", 100));
        menu.AddItem(label, action, enabled);
        menu.ShowAt(x, y);
        _menu = menu;
        MenuCount++;
    }

    // ---------------------------------------------------------------- 退出

    private void ToggleStartup()
    {
        bool enabled = StartupManager.Toggle();
        Log.Info($"开机自启: {(enabled ? "已开启" : "已关闭")}");
    }

    private void RequestExit()
    {
        ExitRequestedForTest = true;
        if (SuppressExitForTest)
        {
            Log.Info("自检模式：忽略退出请求（仅记录）");
            return;
        }
        Exit();
    }

    public void Exit()
    {
        if (!_app.Dispatcher.CheckAccess())
        {
            _app.Dispatcher.BeginInvoke(new Action(Exit));
            return;
        }

        if (_exiting) return;
        _exiting = true;

        Log.Info("退出应用：保存设置");
        SyncAppearanceTo(_settings);
        _settings.Save();

        try { _tray?.Dispose(); } catch { /* ignore */ }
        _tray = null;
        try { _settingsWindow?.Close(); } catch { /* ignore */ }
        try { (_panel as Window)?.Close(); } catch { /* ignore */ }
        try { (_sidebar as Window)?.Close(); } catch { /* ignore */ }
        try { _messages?.Dispose(); } catch { /* ignore */ }
        _messages = null;
        _clock?.Stop();
        _app.Shutdown();
    }

    private async Task RunSelfTestAsync()
    {
        try
        {
            await Task.Delay(900);
            var report = await SelfTestM1.RunAsync(this, _options);
            await SelfTestM2.RunAsync(this, _options, report);
            await SelfTestM1.WriteAsync(report, _options.SelfTestPath!);
            ExitCode = report.AllPassed ? 0 : 2;
            Log.Info($"自检完成: {(report.AllPassed ? "全部通过" : "存在失败项")}，退出码 {ExitCode}");
        }
        catch (Exception ex)
        {
            Log.Info("自检异常: " + ex);
            ExitCode = 3;
        }
        finally
        {
            if (!_options.KeepOpen)
            {
                int code = ExitCode;
                _ = new System.Threading.Timer(_ => Environment.Exit(code), null, 4000, System.Threading.Timeout.Infinite);
                Exit();
            }
        }
    }
}
