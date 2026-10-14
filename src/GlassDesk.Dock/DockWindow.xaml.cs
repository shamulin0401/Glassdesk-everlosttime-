using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.AppServices;
using GlassDesk.Core.Contracts;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.Platform.Shell;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Settings;
using GlassDesk.UI.Theming;

namespace GlassDesk.Dock;

/// <summary>
/// Dock 栏窗口。
/// </summary>
/// <remarks>
/// 结构：窗口只做三件事——摆放（<see cref="PositionOnDisplay"/>，主屏物理像素）、
/// 自动隐藏（<see cref="AutoHideController"/> 状态机 + 主屏窗口内的内容滑动）与把服务交给
/// <see cref="DockTileStrip"/>（磁贴、放大、滚动、命中、点击）。
/// 窗口在 <see cref="OnSourceInitialized"/> 里给自己挂上 <c>WS_EX_NOACTIVATE</c>：
/// 点击 Dock 不把用户正在使用的应用顶到后台。代价是 Dock 弹出的东西
/// （右键菜单、文件夹面板、窗口选择列表）也不会随点击取得焦点，需要键盘交互时
/// 必须显式调 <see cref="WindowActivation.TryBringToForeground"/>。
/// </remarks>
public partial class DockWindow : GlassWindow
{
    /// <summary>启动失败提示在状态行上停留的时长。之后恢复成常规诊断文字。</summary>
    private static readonly TimeSpan FailureNoticeDuration = TimeSpan.FromSeconds(5);

    /// <summary>自动隐藏与滑动的公共帧间隔。</summary>
    /// <summary>
    /// 轮询节拍。起步按 60Hz，一旦渲染循环量到实际帧间隔就跟着它走（覆盖 60–300Hz）。
    /// </summary>
    /// <remarks>
    /// 动画本身不必设帧率：它跑在 <c>CompositionTarget.Rendering</c> 上，合成器给多少帧就跑多少帧。
    /// 这个值只用于 <c>DispatcherTimer</c> 的轮询与「停下」判定，所以它应当等于当前显示器的
    /// 刷新周期，而不是一个写死的 16ms——在 300Hz 屏上 16ms 相当于每五帧才判一次。
    /// </remarks>
    private static TimeSpan FrameInterval => DockRenderLoop.PollInterval;

    /// <summary>托盘展开按钮的边长（DIP）。</summary>
    private const double TrayButtonWidthDip = DockMetricsPolicy.TrayButtonWidthDip;

    /// <summary>系统组内两个子项之间的间隙（DIP）。</summary>
    private const double SystemGroupGapDip = DockMetricsPolicy.SystemGroupGapDip;

    /// <summary>面板底边与 Dock 顶边之间的间隙（DIP）。</summary>
    private const double TrayPanelGapDip = 10;

    /// <summary>运行磁贴组前面那条竖线的宽度（DIP）。</summary>
    private const double RunningSeparatorWidthDip = 1;

    /// <summary>竖线两侧的间隙（DIP）。</summary>
    private const double RunningSeparatorGapDip = 8;

    /// <summary>指针停在运行磁贴上多久才弹预览，避免划过去就闪一堆面板。</summary>
    private static readonly TimeSpan RunningPreviewDelay = TimeSpan.FromMilliseconds(320);

    /// <summary>指针离开磁贴或预览后多久收起；留出移动到预览面板上的时间。</summary>
    private static readonly TimeSpan RunningPreviewGracePeriod = TimeSpan.FromMilliseconds(260);

    /// <summary>预览面板底边与磁贴条上沿之间的间隙（DIP）。</summary>
    private const double RunningPreviewLiftDip = 6;

    private AppServiceHost? _services;
    private DockSettingsHost? _dockSettings;
    private Action<PixelRect?>? _toggleLauncher;
    /// <summary>宿主提供与桌面图标快捷键相同的动作，不依赖快捷键是否启用。</summary>
    public Action? ToggleDesktopIcons { get; set; }

    /// <summary>
    /// 宿主提供的退出动作：右键启动台入口时用它。
    /// </summary>
    /// <remarks>
    /// 退出要收拾单实例互斥体、托盘图标、数据租约等等，只有宿主做得到，
    /// 所以这里只把用户的意图转过去，Dock 自己不动进程。
    /// </remarks>
    public Action? ExitApplication { get; set; }
    private System.Windows.Threading.DispatcherTimer? _failureNoticeTimer;
    private readonly DockTileStrip _tileStrip;

    private readonly List<DockTileModel> _models = [];

    // ---- 右侧系统组：系统托盘与时间日期 ----

    private System.Windows.Threading.DispatcherTimer? _clockTimer;
    private TrayPanelWindow? _trayPanel;
    private bool _trayPanelSuppressingAutoHide;

    // ---- 运行中的应用（Dock 右侧） ----

    private readonly DockTileStrip _runningStrip;
    private readonly RunningTiles _runningTiles = new();
    private List<RunningTile> _running = [];
    private Popup? _runningPreview;
    private System.Windows.Threading.DispatcherTimer? _runningPreviewTimer;
    private RunningTile? _pendingPreviewTile;
    private string? _runningPreviewTileId;
    private bool _pointerOverRunningPreview;

    /// <summary>
    /// 宿主提供的托盘偏好落盘动作。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="ToggleDesktopIcons"/> 同理：Dock 手上只有设置快照，
    /// 写盘要用宿主手里的设置存储，所以由宿主注入这个动作。
    /// </remarks>
    public Func<IReadOnlyList<TrayItemPreference>, bool>? SaveTrayPreferences { get; set; }

    /// <summary>
    /// 宿主提供的「隐藏系统任务栏」状态与动作，转交给托盘面板的开关。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="SaveTrayPreferences"/> 同理：隐藏任务栏要配套守护进程、落盘意图与退出恢复，
    /// 只有宿主做得到，Dock 只负责把面板的请求转过去。
    /// </remarks>
    public Func<bool>? IsTaskbarHidden { get; set; }

    /// <summary>隐藏或恢复系统任务栏；返回是否达到目标状态。</summary>
    public Func<bool, bool>? SetTaskbarHidden { get; set; }

    // ---- 自动隐藏（D-19） ----

    private AutoHideController? _autoHide;
    private System.Windows.Threading.DispatcherTimer? _frameTimer;
    private PixelRect _restingRect;
    private int _fullyHiddenOffsetPixels;
    private bool _revealLocked;
    private bool _edgeRevealedDuringSuppression;
    private bool _launcherContainerActive;
    private Rect? _slidePanelBoundsDip;
    private bool _backdropWasActive;
    private bool _panelWasConcealed;

    /// <summary>Keep the collapsed destination present until the expanded container has returned.</summary>
    public void SetLauncherContainerState(bool expanded)
    {
        _launcherContainerActive = expanded;
        if (expanded && !_performanceSleepRequested && _restingRect.Width > 0)
        {
            _autoHide?.ForceShown(DateTime.UtcNow);
            _slideOffsetPixels = 0;
            ApplySlide(_restingRect, 0);
        }
    }

    /// <summary>当前滑出量（物理像素）。0 = 完全在位；<see cref="SlideOutDistancePixels"/> = 完全隐藏。</summary>
    private double _slideOffsetPixels;

    internal bool IsOpticalSurfaceVisible => _slideOffsetPixels < Math.Max(1, _restingRect.Height - 4);
    internal PixelRect OpticalBackdropBounds => _restingRect;

    private DisplayInfo? CurrentPrimaryDisplay => PrimaryDisplaySession is { } session
        ? session.IsAvailable ? session.Display : null
        : DisplayService.Primary();

    protected override byte ResolveAcrylicTintAlpha(GlassPalette palette) => palette.IsDark ? (byte)54 : (byte)66;

    protected override Geometry CreatePanelGeometryCore(Rect bounds, double radius) =>
        SquircleGlassGeometry.Create(bounds, radius);

    protected override bool ContainsPanelGeometryCore(Rect bounds, double radius, Point point) =>
        SquircleGlassGeometry.Contains(_slidePanelBoundsDip ?? bounds, radius, point);

    protected override bool TryGetPanelGeometryEdgeCore(Rect bounds, double radius, Point point,
        out double depth, out Vector inwardNormal) =>
        SquircleGlassGeometry.TryGetEdge(_slidePanelBoundsDip ?? bounds, radius, point, out depth, out inwardNormal);

    protected override bool UsesSquirclePanelGeometryCore => true;

    public DockWindow()
    {
        InitializeComponent();
        InitializeEntryHover();
        PanelCornerRadius = new CornerRadius(40);
        GlassSurface.IsBackdropActive = () => IsOpticalSurfaceVisible;

        _tileStrip = new DockTileStrip();
        _tileStrip.TileClicked += OnTileClicked;
        _tileStrip.TileContextMenuRequested += OnTileContextMenuRequested;
        _tileStrip.ReorderFinished += (_, _) => RefreshTiles();
        _tileStrip.UnpinRequested += OnUnpinRequested;
        _tileStrip.SetReorderModel(new DockReorderModel(SaveDockOrder));
        TileStripHost.Content = _tileStrip;

        // 运行中的应用是另一条独立的磁贴条。
        // 刻意不给它排序模型：它的内容由系统此刻开着什么决定，拖动排序既无意义，
        // 也不能落进 dock-layout.json（那份布局只记「用户固定了哪些应用」）。
        _runningStrip = new DockTileStrip();
        _runningStrip.TileClicked += OnRunningTileClicked;
        RunningTileStripHost.Content = _runningStrip;
        RunningTileStripHost.MouseMove += OnRunningStripMouseMove;
        RunningTileStripHost.MouseLeave += OnRunningStripMouseLeave;

        // 常驻的内置入口不依赖宿主：构造完就该出现在磁贴行上。
        RefreshTiles();

        // Dock 自己不可见时（性能休眠会整个 Hide），悬在它上方的托盘面板也必须跟着走。
        IsVisibleChanged += OnDockVisibilityChanged;
    }

    /// <summary>
    /// 排序提交的落盘路径：只在这里写一次（R-08），并且**只写应用库里的真实条目**。
    /// </summary>
    /// <remarks>
    /// 排序模型给过来的列表本应只含应用磁贴，这里再校验一遍：
    /// 去掉内置入口（<c>entry:*</c>）、去掉库里不存在的 id、去重、保持顺序。
    /// 布局文件记的是「用户固定了哪些应用、什么顺序」，混进伪 id 就不再表示这件事了。
    /// </remarks>
    private void SaveDockOrder(IReadOnlyList<string> order)
    {
        if (_services is null)
        {
            return;
        }

        var sanitized = DockLayoutPolicy.Sanitize(order, id => _services.Library.Find(id) is not null);

        _services.Layouts.Dock.PinnedItemIds = sanitized;
        _services.Layouts.SaveDock();
    }

    /// <summary>
    /// 清理布局里不属于应用库的 id；有变化才写盘。
    /// </summary>
    /// <remarks>
    /// 早先的版本把三个内置入口（<c>entry:launcher</c> 等）写进了布局，已在文件里的那些
    /// 要在加载时清掉，同时**保持原有应用顺序**。
    ///
    /// <para>
    /// 安全前提：只有当应用库**确实加载出了条目**时才清洗。
    /// 库为空可能是加载失败，此时所有 id 都「查不到」，照着清洗会把整份布局抹掉。
    /// </para>
    /// </remarks>
    public void SanitizePinnedLayout()
    {
        if (_services is null || _services.Library.Items.Count == 0)
        {
            return;
        }

        var pinned = _services.Layouts.Dock.PinnedItemIds;
        var sanitized = DockLayoutPolicy.Sanitize(pinned, id => _services.Library.Find(id) is not null);

        if (DockLayoutPolicy.SameOrder(pinned, sanitized))
        {
            return;
        }

        var dropped = pinned.Count - sanitized.Count;
        _services.Layouts.Dock.PinnedItemIds = sanitized;
        try
        {
            _services.Layouts.SaveDock();
        }
        catch (StorageException)
        {
            AppendLayoutNotice("清理旧固定项时无法保存；磁盘布局已保留，启动继续。请检查数据目录的写入权限。");
            return;
        }

        AppendLayoutNotice($"布局文件里清理了 {dropped} 个不属于应用库的固定项（内置入口或已失效的条目）。");
    }

    /// <summary>
    /// 拖出取消固定（D-03）：只取消固定，<strong>不删</strong>应用库记录——
    /// 启动台里这一项还在。
    /// </summary>
    /// <remarks>
    /// 内置入口不受这一路径影响：它们不是固定项，也没有拖拽处理器。
    /// 这里再挡一道，防止将来的调用方误把 <c>entry:*</c> 递进来。
    /// </remarks>
    private void OnUnpinRequested(object? sender, string entryId)
    {
        if (_services is null || DockLayoutPolicy.IsEntryTile(entryId))
        {
            return;
        }

        if (!_services.Layouts.UnpinFromDock(entryId))
        {
            return;
        }

        try
        {
            _services.Layouts.SaveDock();
        }
        catch (StorageException)
        {
            ShowFailureNotice("未能保存取消固定操作，原固定项已恢复。");
        }

        RefreshTiles();
    }

    /// <summary>把一条与布局有关的话写进启动日志，便于事后核对自愈行为。</summary>
    private void AppendLayoutNotice(string message)
    {
        if (_services is null)
        {
            return;
        }

        try
        {
            _services.Paths.EnsureCreated();
            File.AppendAllText(
                Path.Combine(_services.Paths.LogDirectory, "startup.log"),
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] Dock 布局：{message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 记日志失败绝不能影响界面。
        }
    }

    /// <summary>悬停时接受可用的启动台应用条目或文件列表。</summary>
    private void OnExternalDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(LauncherDragContract.SlotDataFormat)
            && e.Data.GetData(LauncherDragContract.SlotDataFormat) is string itemId)
        {
            e.Effects = _services?.Library.Find(itemId) is { IsUsable: true }
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop, true)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// 外部拖入导入（D-01）：资源管理器拖来的文件走导入服务，
    /// 成功的条目顺手固定到 Dock 尾部——否则用户拖完了什么都没看见。
    /// </summary>
    /// <remarks>
    /// 注册归属（修订计划第 5 条）：WPF 对窗口句柄自己注册 OLE 拖放目标
    /// （探针 C 实测证实），这里只用 WPF 的 Drop 事件，绝不再 RegisterDragDrop。
    /// </remarks>
    private void OnExternalDrop(object sender, DragEventArgs e)
    {
        if (_services is null)
        {
            return;
        }

        if (e.Data.GetDataPresent(LauncherDragContract.SlotDataFormat)
            && e.Data.GetData(LauncherDragContract.SlotDataFormat) is string itemId)
        {
            e.Handled = true;
            PinLauncherItem(itemId, e);
            return;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop, true)) return;
        e.Handled = true;

        if (e.Data.GetData(DataFormats.FileDrop, true) is not string[] { Length: > 0 } sources)
        {
            return;
        }

        // 拖入期间自动隐藏计时被抑制，导入完成后恢复。
        _autoHide?.PushSuppress(DateTime.UtcNow);

        try
        {
            var requests = sources.Select(source => new ImportRequest { Source = source }).ToList();
            var result = _services.Import.ImportItems(requests);

            // 已固定的 id 与「本次新增/已在库」合并——重复条目也要固定：
            // 把一条从 Dock 移出的应用再拖回来时，导入报的是「重复」，
            // 只看 Added 会让这个手势完全没有反应（条目本来就在库里）。
            var pinned = _services.Layouts.Dock.PinnedItemIds;
            var merged = DockPinMerge.Merge(
                pinned,
                result.Added.Select(entry => entry.Id),
                result.Duplicates.Select(entry => entry.Id));

            var newlyPinned = merged.Count - pinned.Count;

            if (newlyPinned > 0)
            {
                _services.Layouts.Dock.PinnedItemIds = merged;
                try
                {
                    _services.Layouts.SaveDock();
                }
                catch (StorageException)
                {
                    ShowFailureNotice("已导入应用库，但未能保存 Dock 固定；原固定顺序已恢复。");
                    RefreshTiles();
                    return;
                }
            }

            ShowTemporaryNotice(result.Failures.Count == 0
                ? $"已固定 {newlyPinned} 项（新增 {result.Added.Count}，已在库 {result.Duplicates.Count}）。"
                : $"已固定 {newlyPinned} 项，{result.Failures.Count} 项失败。");

            RefreshTiles();
        }
        finally
        {
            _autoHide?.PopSuppress(DateTime.UtcNow);
        }
    }

    private void PinLauncherItem(string itemId, DragEventArgs e)
    {
        var entry = _services?.Library.Find(itemId);
        if (entry is not { IsUsable: true })
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        var pinned = _services!.Layouts.Dock.PinnedItemIds;
        var merged = DockPinMerge.Merge(pinned, [entry.Id], []);
        if (merged.Count == pinned.Count)
        {
            ShowTemporaryNotice("此应用已在 Dock 中。");
            e.Effects = DragDropEffects.Copy;
            RefreshTiles();
            return;
        }

        _services.Layouts.Dock.PinnedItemIds = merged;
        try
        {
            _services.Layouts.SaveDock();
            ShowTemporaryNotice("已添加到 Dock。");
            e.Effects = DragDropEffects.Copy;
            RefreshTiles();
        }
        catch (StorageException)
        {
            ShowFailureNotice("未能保存 Dock 布局，原有图标顺序已恢复。");
            e.Effects = DragDropEffects.None;
            RefreshTiles();
        }
    }

    /// <summary>生效中的程序坞设置；尚未附加宿主时是默认值。</summary>
    public DockSettings Settings => _dockSettings?.Settings ?? new DockSettings();

    /// <summary>
    /// 当前磁贴总数（固定项 + 三个内置入口）。诊断与测试用。
    /// </summary>
    public int TileCount => _models.Count;

    /// <summary>
    /// 把宿主建好的东西交给 Dock：应用服务入口、程序坞设置通知口与「切换启动台」回调。
    /// </summary>
    /// <remarks>
    /// 结构约束（开工说明 4.7）：Dock 经 <see cref="AppServiceHost"/> 使用应用服务，
    /// 绝不引用宿主工程 <c>GlassDesk.App</c>；设置变更经
    /// <see cref="DockSettingsHost.Changed"/> 推进来，Dock 不轮询设置文件；
    /// 运行状态经 <see cref="RunningAppTracker.Changed"/> 推进来，
    /// Dock 不做任何窗口查询。
    /// </remarks>
    public void AttachServices(AppServiceHost services, DockSettingsHost settings, Action toggleLauncher)
    {
        ArgumentNullException.ThrowIfNull(toggleLauncher);
        AttachServices(services, settings, _ => toggleLauncher());
    }

    public void AttachServices(AppServiceHost services, DockSettingsHost settings, Action<PixelRect?> toggleLauncher)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(toggleLauncher);

        if (_dockSettings is not null)
        {
            _dockSettings.Changed -= OnDockSettingsChanged;
        }

        if (_services is not null)
        {
            _services.Running.Changed -= OnRunningChanged;
        }

        _services = services;
        _dockSettings = settings;
        _toggleLauncher = toggleLauncher;

        _dockSettings.Changed += OnDockSettingsChanged;
        _services.Running.Changed += OnRunningChanged;

        // 布局里可能残留旧版本写进去的内置入口（entry:*）或已经不存在的 id，
        // 在第一次画磁贴之前清掉。
        SanitizePinnedLayout();

        RebuildAutoHide();
        RefreshTiles();
        RefreshFromSettings();
    }

    /// <summary>Raise the non-activating Dock above Launcher while it is a drag target.</summary>
    public void RaiseAboveLauncherDuringDrag() => _ = WindowStyles.BringToTopWithoutActivation(Handle);

    /// <summary>Dock 使用栏自身的圆角半径，而不是启动台的。</summary>
    protected override double ResolveCornerRadius(AppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return Math.Max(40, appearance.DockCornerRadius) * DockScale;
    }

    // ---- 磁贴 ----

    /// <summary>
    /// 按布局文档的固定顺序重建磁贴行。
    /// </summary>
    /// <remarks>
    /// 固定顺序来自 <c>dock-layout.json</c> 的稠密有序列表：位置即索引。
    /// 布局里引用了但库里已经没有的 id 直接跳过——那只会发生在数据被
    /// 手工编辑过的时候，正常路径上「从 Dock 移除」会同步更新布局。
    /// </remarks>
    public void RefreshTiles()
    {
        if (_performanceSleepRequested) { _tilesDirty = true; return; }
        var settings = Settings;
        var tracker = _services?.Running;
        var dotBrush = ThemeService.Current?.Palette.ForegroundBrush;

        _models.Clear();

        // 固定项依赖应用服务；未附加宿主时（构造后立刻查询的场合）只少固定项，
        // 内置入口独立于固定项，由 Dock 设置控制可见性。
        if (_services is not null)
        {
            foreach (var id in _services.Layouts.Dock.PinnedItemIds)
            {
                var entry = _services.Library.Find(id);

                if (entry is null || !entry.IsUsable)
                {
                    continue;
                }

                _models.Add(CreateTile(entry, settings, tracker!, dotBrush));
            }
        }

        _tileStrip.SetTiles(_models);
        RefreshRunningTiles();
        ApplyStripMetrics();
    }

    /// <summary>
    /// 重建 Dock 右侧「正在运行的应用」那一条。
    /// </summary>
    /// <remarks>
    /// 与固定图标分开重建：固定图标只在库或布局变化时变，而这一条窗口一关就得消失。
    /// 只重建这一条也避免把用户的悬停、放大动画无谓地重置掉。
    /// </remarks>
    private void RefreshRunningTiles()
    {
        _running = [.. _runningTiles.Build(_services, Settings, DockScale)];

        var scale = DockScale;
        var dotBrush = ThemeService.Current?.Palette.ForegroundBrush;
        var models = new List<DockTileModel>(_running.Count);

        foreach (var tile in _running)
        {
            models.Add(new DockTileModel
            {
                EntryId = tile.Id,
                DisplayName = tile.DisplayName,
                ToolTipText = tile.ToolTip,
                Icon = tile.Icon,
                DotBrush = dotBrush,
                IconSize = DockSettings.FixedIconSize * scale,

                // 这一条本身就是「正在运行」的表示，再点一个运行圆点是重复的。
                RunningVisibility = Visibility.Collapsed,
            });
        }

        _runningStrip.SetTiles(models);

        var anyRunning = models.Count > 0;
        RunningTileStripHost.Visibility = anyRunning ? Visibility.Visible : Visibility.Collapsed;

        // 竖线由 ApplyStripMetrics 决定：它才知道固定区是不是空的，
        // 而没有固定项时画一条竖线只会是一道悬空的线。
        if (!anyRunning)
        {
            CloseRunningPreview();
        }
    }

    /// <summary>按 id 取回一条运行磁贴；找不到返回 null（窗口可能刚关掉）。</summary>
    private RunningTile? FindRunningTile(string id)
    {
        foreach (var tile in _running)
        {
            if (string.Equals(tile.Id, id, StringComparison.Ordinal))
            {
                return tile;
            }
        }

        return null;
    }

    /// <summary>
    /// 点运行磁贴：一个窗口就直接恢复并激活，多个就列出让用户选。
    /// </summary>
    /// <remarks>
    /// 走 <see cref="LaunchService.ActivateWindow"/> 而不是裸的 <c>SetForegroundWindow</c>：
    /// 后者不会把最小化的窗口还原，而这一条恰恰以「包括最小化窗口」为职责。
    /// </remarks>
    private void OnRunningTileClicked(object? sender, string tileId)
    {
        var tile = FindRunningTile(tileId);

        if (tile is null)
        {
            return;
        }

        CloseRunningPreview();

        // 每次点击都重新读一遍这组窗口：快照只保证句柄集合，最小化与还原状态可能已经变了。
        var windows = RefreshRunningWindows(tile);

        if (windows.Count == 0)
        {
            // 图标还在，窗口已经全关了：立刻把这一条收掉，别让用户点了个空。
            RefreshRunningTiles();
            ApplyStripMetrics();
            return;
        }

        if (windows.Count > 1)
        {
            ShowWindowChooser(windows.Select(window => new WindowCandidate
            {
                Handle = window.Handle,
                Title = window.Title,
            }).ToList());
            return;
        }

        var outcome = LaunchService.ActivateWindow(windows[0].Handle);

        if (outcome.Outcome == ActivationOutcome.Failed)
        {
            ShowFailureNotice(outcome.Message ?? "没能把那个窗口切到前台。");
        }
    }

    /// <summary>把这组窗口按当前窗口快照重新求出来；同一应用可能刚开了新窗口或关掉了旧窗口。</summary>
    private IReadOnlyList<RunningWindow> RefreshRunningWindows(RunningTile tile)
    {
        if (_services is null)
        {
            return tile.Group.Windows;
        }

        var updated = _runningTiles.Build(_services, Settings, DockScale);
        var refreshed = updated.FirstOrDefault(candidate => string.Equals(candidate.Id, tile.Id, StringComparison.Ordinal));
        return refreshed?.Group.Windows ?? [];
    }

    // ---- 悬停预览 ----

    /// <summary>
    /// 指针移到运行磁贴上：等一小会儿再弹预览。
    /// </summary>
    /// <remarks>
    /// 用宿主控件的鼠标事件加磁贴条的公开命中测试，而不是给磁贴条加一个悬停事件：
    /// 命中判定必须与它自己画出来的那一帧一致，只有它知道；而重建这一帧的时机
    /// 由它自己掌握，外人去猜会命中到上一个位置。
    /// </remarks>
    private void OnRunningStripMouseMove(object sender, MouseEventArgs e)
    {
        if (_running.Count == 0)
        {
            CloseRunningPreview();
            return;
        }

        var index = _runningStrip.ResolveHitAtViewportX(e.GetPosition(_runningStrip).X);

        if (index < 0 || index >= _running.Count)
        {
            // 停在两个图标之间的空隙上：先不动，等他真的移开或落到另一个磁贴上。
            return;
        }

        var tile = _running[index];

        if (string.Equals(_runningPreviewTileId, tile.Id, StringComparison.Ordinal) && _runningPreview is { IsOpen: true })
        {
            return;
        }

        _runningPreviewTileId = tile.Id;
        ScheduleRunningPreview(tile);
    }

    private void OnRunningStripMouseLeave(object sender, MouseEventArgs e)
    {
        _runningPreviewTileId = null;
        ScheduleRunningPreviewClose();
    }

    private void EnsureRunningPreviewTimer()
    {
        if (_runningPreviewTimer is not null)
        {
            return;
        }

        _runningPreviewTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = RunningPreviewDelay,
        };
        _runningPreviewTimer.Tick += OnRunningPreviewTick;
    }

    private void ScheduleRunningPreview(RunningTile tile)
    {
        EnsureRunningPreviewTimer();
        _pendingPreviewTile = tile;
        _runningPreviewTimer!.Stop();
        _runningPreviewTimer.Interval = RunningPreviewDelay;
        _runningPreviewTimer.Start();
    }

    /// <summary>延后收起：指针从磁贴移到预览面板上时不能被当成离开。</summary>
    private void ScheduleRunningPreviewClose()
    {
        if (_runningPreviewTimer is null)
        {
            return;
        }

        _pendingPreviewTile = null;
        _runningPreviewTimer.Stop();
        _runningPreviewTimer.Interval = RunningPreviewGracePeriod;
        _runningPreviewTimer.Start();
    }

    private void OnRunningPreviewTick(object? sender, EventArgs e)
    {
        _runningPreviewTimer?.Stop();

        if (_pendingPreviewTile is { } tile)
        {
            ShowRunningPreview(tile);
            return;
        }

        if (!_pointerOverRunningPreview)
        {
            CloseRunningPreview();
        }
    }

    private void ShowRunningPreview(RunningTile tile)
    {
        CloseRunningPreview();

        var content = new StackPanel
        {
            MinWidth = 150,
            MaxWidth = 340,
        };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 4, 8, 6),
        };

        if (tile.Icon is not null)
        {
            header.Children.Add(new Image
            {
                Source = tile.Icon,
                Width = 18,
                Height = 18,
                Margin = new Thickness(0, 0, 8, 0),
            });
        }

        header.Children.Add(new TextBlock
        {
            Text = tile.DisplayName,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        content.Children.Add(header);

        var windows = tile.Group.Windows;
        var list = new ListBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
        };

        for (var index = 0; index < windows.Count; index++)
        {
            var window = windows[index];
            var title = string.IsNullOrWhiteSpace(window.Title) ? $"窗口 {index + 1}" : window.Title;

            list.Items.Add(new ListBoxItem
            {
                Content = window.IsMinimized ? title + "（已最小化）" : title,
                Padding = new Thickness(10, 5, 10, 5),
                Tag = window.Handle,
            });
        }

        if (list.Items.Count > 0)
        {
            list.SelectedIndex = 0;
        }

        content.Children.Add(list);

        var palette = ThemeService.Current?.Palette;
        var panel = new PhoneBorder
        {
            Background = palette?.ResolvePanelBrush(false) ?? Brushes.DimGray,
            BorderBrush = palette?.PanelBorderBrush ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(4),
            Child = content,
        };

        var popup = new Popup
        {
            Child = panel,
            Placement = PlacementMode.Custom,
            PlacementTarget = _runningStrip,
            StaysOpen = true,
            AllowsTransparency = true,
        };

        // 悬停到面板上不算离开；离开面板才开始计时收起。
        panel.MouseEnter += (_, _) =>
        {
            _pointerOverRunningPreview = true;
            _runningPreviewTimer?.Stop();
        };
        panel.MouseLeave += (_, _) =>
        {
            _pointerOverRunningPreview = false;
            ScheduleRunningPreviewClose();
        };

        list.PreviewMouseLeftButtonUp += (_, args) =>
        {
            var handle = WindowChooserHit.Resolve(list, args.OriginalSource);

            if (handle != 0)
            {
                ActivatePreviewWindow(handle);
            }
        };

        var center = RunningTileCenterX(tile.Id);

        popup.CustomPopupPlacementCallback = (popupSize, _, _) =>
        [
            new CustomPopupPlacement(
                new Point(center - (popupSize.Width / 2), -popupSize.Height - RunningPreviewLiftDip),
                PopupPrimaryAxis.Horizontal),
        ];

        _runningPreview = popup;

        // 预览悬在 Dock 上方，指针一进面板就离开了 Dock 的命中区。
        _autoHide?.PushSuppress(DateTime.UtcNow);
        popup.IsOpen = true;
    }

    private void ActivatePreviewWindow(nint handle)
    {
        CloseRunningPreview();

        var outcome = LaunchService.ActivateWindow(handle);

        if (outcome.Outcome == ActivationOutcome.Failed)
        {
            ShowFailureNotice(outcome.Message ?? "没能把那个窗口切到前台。");
        }
    }

    /// <summary>被悬停磁贴的水平中心（相对磁贴条），用来把预览对准它。</summary>
    private double RunningTileCenterX(string id)
    {
        var painted = _runningStrip.PaintedTiles;
        var width = DockSettings.FixedIconSize * DockScale;

        foreach (var tile in painted)
        {
            if (string.Equals(tile.EntryId, id, StringComparison.Ordinal))
            {
                return tile.Left + (width * tile.Scale / 2);
            }
        }

        return Math.Max(0, _runningStrip.ActualWidth / 2);
    }

    private void CloseRunningPreview()
    {
        _runningPreviewTimer?.Stop();
        _pendingPreviewTile = null;
        _runningPreviewTileId = null;
        _pointerOverRunningPreview = false;

        if (_runningPreview is null)
        {
            return;
        }

        var popup = _runningPreview;
        _runningPreview = null;
        popup.IsOpen = false;
        _autoHide?.PopSuppress(DateTime.UtcNow);
    }

    private DockTileModel CreateTile(AppEntry entry, DockSettings settings, RunningAppTracker tracker, Brush? dotBrush)
    {
        var tooltip = entry.DisplayName;

        if (entry.IsTargetMissing)
        {
            // 失效条目（R-07）现在先在提示里说清楚；「显示为失效」的视觉
            // 与右键重新定位入口在批次 5 接入。
            tooltip += "（目标已不在原处，可重新定位）";
        }

        return new DockTileModel
        {
            EntryId = entry.Id,
            DisplayName = entry.DisplayName,
            ToolTipText = tooltip,
            Icon = LoadTileIcon(entry),
            DotBrush = dotBrush,
            IconSize = DockSettings.FixedIconSize * DockScale,
            TileMargin = new Thickness(DockSettings.FixedIconSpacing * DockScale / 2, 0, DockSettings.FixedIconSpacing * DockScale / 2, 0),
            IsMissing = entry.IsTargetMissing,
            RunningVisibility = tracker.IsRunning(entry)
                ? Visibility.Visible
                : Visibility.Collapsed,
        };
    }

    /// <summary>
    /// 把尺寸与滚动封顶交给磁贴条。
    /// </summary>
    /// <remarks>
    /// 封顶宽度 = 目标显示器工作区的 DIP 宽度 − 面板内边距、已启用入口与放大余量。
    /// 固定项超出时磁贴条自身横向滚动（修订计划第 6 条），窗口永远不出工作区。
    /// </remarks>
    private void ApplyStripMetrics()
    {
        var settings = Settings;
        var display = CurrentPrimaryDisplay;
        var scale = DockScale;
        DockContentLayer.Margin = new Thickness(10 * scale, 8 * scale, 10 * scale, 8 * scale);
        if ((LauncherButton.Visibility == Visibility.Visible) != settings.ShowLauncherEntry
            || (ThisPcButton.Visibility == Visibility.Visible) != settings.ShowThisPcEntry
            || (DesktopIconsButton.Visibility == Visibility.Visible) != settings.ShowDesktopIconsEntry)
            ResetEntryHover();
        LauncherButton.Visibility = settings.ShowLauncherEntry ? Visibility.Visible : Visibility.Collapsed;
        ThisPcButton.Visibility = settings.ShowThisPcEntry ? Visibility.Visible : Visibility.Collapsed;
        DesktopIconsButton.Visibility = settings.ShowDesktopIconsEntry ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { LauncherButton, ThisPcButton, DesktopIconsButton })
        {
            button.Width = 48 * scale;
            button.Height = 64 * scale;
            button.Margin = new Thickness(0, 0, 8 * scale, 0);
        }
        LaunchpadTile.Width = LaunchpadTile.Height = ThisPcTile.Width = ThisPcTile.Height
            = DesktopIconsTile.Width = DesktopIconsTile.Height = 48 * scale;
        LaunchpadTile.CornerRadius = new CornerRadius(16 * scale);
        PanelCornerRadius = new CornerRadius(40 * scale);

        // 右侧系统组：底板只在至少一个子项可见时出现，两个子项各自独立。
        var showTrayButton = settings.ShowSystemTray;
        var showClock = settings.ShowClock;
        var systemGroupVisible = showTrayButton || showClock;
        SystemGroupHost.Visibility = systemGroupVisible ? Visibility.Visible : Visibility.Collapsed;
        TrayPanelButton.Visibility = showTrayButton ? Visibility.Visible : Visibility.Collapsed;
        ClockHost.Visibility = showClock ? Visibility.Visible : Visibility.Collapsed;

        if (systemGroupVisible)
        {
            SystemGroupHost.Margin = new Thickness(SystemGroupGapDip * scale, 0, 0, 0);
            SystemGroupHost.Padding = new Thickness(6 * scale, 0, 6 * scale, 0);
            TrayPanelButton.Width = TrayPanelButton.Height = TrayButtonWidthDip * scale;
            ClockHost.Margin = new Thickness((showTrayButton ? SystemGroupGapDip : 0) * scale, 0, 2 * scale, 0);
            ClockTimeText.FontSize = 15 * scale;
            ClockDateText.FontSize = 10.5 * scale;
        }

        var reservedWidthDip = BuiltinEntryWidthDip + SystemGroupWidthDip;

        var contentLimit = display is null
            ? 0.0
            : Math.Max(1, display.ToDips((int)display.WorkArea.Width) - 32 - (28 + reservedWidthDip) * scale);

        if (settings.FrameLengthDip > 0)
            contentLimit = Math.Max(1, ResolveEffectiveFrameLengthDip(settings.FrameLengthDip, display) - (22 + reservedWidthDip) * scale);

        // 两条磁贴条共用一份内容预算，必须切开而不是各拿一份：
        // 各拿一份会让窗口宽度等于两份封顶之和，直接冲出工作区。
        var iconSize = DockSettings.FixedIconSize * scale;
        var spacing = DockSettings.FixedIconSpacing * scale;
        var runningVisible = _running.Count > 0;
        var separatorWidth = runningVisible && _models.Count > 0 ? RunningSeparatorWidthDip * scale : 0;
        var stripsBudget = Math.Max(1, contentLimit - separatorWidth);

        // 没有固定项时竖线不出现：那会是一道没有分开任何东西的悬空的线。
        RunningGroupSeparator.Visibility = separatorWidth > 0 ? Visibility.Visible : Visibility.Collapsed;

        var pinnedLimit = stripsBudget;

        if (runningVisible)
        {
            // 固定图标是用户自己摆的，优先满足；至少给右侧留一个图标的位置，
            // 否则它整条会消失——MaxWidth 传 0 会被 <c>DockTileStrip</c> 当成「不封顶」。
            var oneSlot = Math.Max(1, DockMagnification.RequiredLength(
                1, iconSize, spacing, DockSettings.FixedMagnificationPeak));
            var pinnedCap = Math.Max(1, stripsBudget - oneSlot);
            var pinnedNatural = Math.Max(0, _tileStrip.RequiredLength);

            pinnedLimit = Math.Min(pinnedNatural, pinnedCap);

            var runningLimit = Math.Max(1, stripsBudget - pinnedLimit);

            RunningGroupSeparator.Width = Math.Max(1, separatorWidth);
            RunningGroupSeparator.Margin = new Thickness(
                RunningSeparatorGapDip * scale,
                10 * scale,
                RunningSeparatorGapDip * scale,
                10 * scale);
            _runningStrip.SetMetrics(
                iconSize,
                spacing,
                DockSettings.FixedMagnificationPeak,
                8 * scale,
                runningLimit);
        }

        _tileStrip.SetMetrics(
            iconSize,
            spacing,
            DockSettings.FixedMagnificationPeak,
            8 * scale,
            pinnedLimit);
    }

    private double DockScale => double.IsFinite(Settings.SizeScale)
        ? Math.Clamp(Settings.SizeScale, DockSettings.MinimumSizeScale, DockSettings.MaximumSizeScale) : 1;

    private double BuiltinEntryWidthDip => DockMetricsPolicy.BuiltinEntryWidthDip(Settings);

    /// <summary>
    /// 右侧系统组占用的宽度（DIP，未乘尺寸倍率）。
    /// </summary>
    /// <remarks>
    /// 是预留值而不是实测值：它参与磁贴条的内容封顶，而封顶又决定磁贴条的实际宽度，
    /// 去读 <c>DesiredSize</c> 会绕成一个循环依赖。公式与设置窗口共用
    /// <see cref="DockMetricsPolicy"/>，否则滑块允许的长度会与 Dock 认可的下限不一致。
    /// </remarks>
    private double SystemGroupWidthDip => DockMetricsPolicy.SystemGroupWidthDip(Settings);

    private double ResolveEffectiveFrameLengthDip(double requested, DisplayInfo? display)
    {
        var entryMinimum = DockMetricsPolicy.MinimumFrameLengthDip(Settings);
        if (display is null)
            return Math.Clamp(requested, entryMinimum, DockSettings.MaximumFrameLengthDip);
        var maxWidth = Math.Max(64, display.ToDips((int)display.WorkArea.Width) - 32);
        maxWidth = Math.Min(DockSettings.MaximumFrameLengthDip, maxWidth);
        var minimum = Math.Min(entryMinimum, maxWidth);
        return Math.Clamp(requested, minimum, maxWidth);
    }

    private void ApplyFrameLength()
    {
        var requested = Settings.FrameLengthDip;
        if (requested <= 0)
        {
            SizeToContent = System.Windows.SizeToContent.WidthAndHeight;
            Width = double.NaN;
            return;
        }

        var display = CurrentPrimaryDisplay;
        SizeToContent = System.Windows.SizeToContent.Height;
        Width = ResolveEffectiveFrameLengthDip(requested, display);
    }

    /// <summary>取一枚磁贴的图标：用户自选的优先，其次缓存里提取的，没有就空着。</summary>
    private BitmapImage? LoadTileIcon(AppEntry entry)
    {
        var custom = entry.CustomIconPath;

        if (!string.IsNullOrEmpty(custom) && File.Exists(custom))
        {
            return LoadBitmap(custom);
        }

        if (_services is null || string.IsNullOrEmpty(entry.IconSource))
        {
            return null;
        }

        var cached = _services.Icons.PathFor(entry.IconSource);
        if (!File.Exists(cached))
        {
            cached = _services.Icons.EnsureFor(entry.IconSource)?.Path ?? cached;
        }

        return File.Exists(cached) ? LoadBitmap(cached) : null;
    }

    private static BitmapImage? LoadBitmap(string path)
    {
        try
        {
            // OnLoad：把像素读进内存就释放文件句柄，否则用户在资源管理器里
            // 删不掉正在被 Dock 显示的那张图标。
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UriFormatException or System.IO.FileFormatException)
        {
            return null;
        }
    }

    /// <summary>磁贴点击（D-08）：内置入口分派；文件夹条目弹面板（D-11）；其余先激活后启动。</summary>
    private void OnTileClicked(object? sender, string entryId)
    {
        if (entryId.StartsWith("entry:", StringComparison.Ordinal))
        {
            RunEntryAction(entryId["entry:".Length..]);
            return;
        }

        if (_services is null)
        {
            return;
        }

        var entry = _services.Library.Find(entryId);

        if (entry is not null && entry.Kind == AppItemKind.Folder && !entry.IsTargetMissing)
        {
            ShowFolderPanel(entry);
            return;
        }

        var outcome = _services.Launch.ActivateOrLaunch(entryId);

        switch (outcome.Outcome)
        {
            case ActivationOutcome.MultipleWindowsFound:
                ShowWindowChooser(outcome.Candidates);
                break;

            case ActivationOutcome.Failed:
                ShowFailureNotice(outcome.Message);
                break;

            default:
                // Activated / Launched 不需要额外反馈：窗口到前台或应用启动本身就是反馈。
                break;
        }
    }

    /// <summary>内置入口的动作分派。</summary>
    private void RunEntryAction(string name)
    {
        switch (name)
        {
            case "launcher":
                // D-12：入口磁贴触发宿主切换回调（Dock 不直接持有启动台）。
                RequestLauncherToggle();
                break;

            case "showdesktop":
                // D-13：实现依据探针 D 的实测结论（逐窗口 SW_MINIMIZE）。
                _ = DesktopCommands.ShowDesktop();
                break;

            case "recyclebin":
                // D-14：回收站是内置入口，直接走外壳；绝不为塞进应用库而
                // 放宽导入的存在性检查（阶段 4 开工说明 4.6 的明确告诫）。
                var opened = ShellExecutor.Open("shell:RecycleBinFolder", null, null);
                if (!opened.Started)
                {
                    ShowFailureNotice("回收站没能打开。");
                }

                break;

            default:
                break;
        }
    }

    /// <summary>
    /// 文件夹弹出面板（D-11）：列出文件夹里的条目，点击经外壳打开。
    /// </summary>
    /// <remarks>
    /// 面板打开时取得键盘焦点，支持 Esc；关闭时只在焦点仍属于 Dock 的情况下
    /// 归还原窗口，不抢回用户已切换到的其他应用。面板期间抑制自动隐藏。
    /// 面板里的点击走同一条外壳路径（<see cref="ShellExecutor"/>）。
    /// </remarks>
    private void ShowFolderPanel(AppEntry folderEntry)
    {
        // 抑制压在弹层的 Opened 上、解除在 Closed 上（见下面），**不在打开之前**压：
        // PushSuppress 只是个计数、没有超时兜底，弹层万一没能打开，Closed 就不会来，
        // 这个计数会一直挂着，Dock 从此不再自动隐藏。
        var foregroundBefore = WindowControl.GetForegroundWindow();

        var palette = ThemeService.Current?.Palette;

        var header = new TextBlock
        {
            Text = folderEntry.DisplayName,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(6, 4, 6, 2),
        };

        var list = new StackPanel();

        _ = list.Children.Add(header);
        _ = list.Children.Add(PanelItem("打开这个文件夹", () =>
        {
            var open = ShellExecutor.Open(folderEntry.Target, null, null);
            if (!open.Started)
            {
                ShowFailureNotice("文件夹没能打开。");
            }
        }));

        string[] children;

        try
        {
            children = Directory
                .EnumerateFileSystemEntries(folderEntry.Target)
                .OrderBy(Path.GetFileName, StringComparer.CurrentCulture)
                .Take(50)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            children = [];
        }

        if (children.Length == 0)
        {
            _ = list.Children.Add(new TextBlock
            {
                Text = "（空文件夹，或读不到内容）",
                Margin = new Thickness(10, 4, 10, 6),
            });
        }

        foreach (var child in children)
        {
            var path = child;
            _ = list.Children.Add(PanelItem(Path.GetFileName(path), () =>
            {
                var open = ShellExecutor.Open(path, null, null);
                if (!open.Started)
                {
                    ShowFailureNotice($"没能打开：{Path.GetFileName(path)}");
                }
            }));
        }

        var panel = new PhoneBorder
        {
            Background = palette?.ResolvePanelBrush(false) ?? Brushes.DimGray,
            BorderBrush = palette?.PanelBorderBrush ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            MinWidth = 220,
            MaxHeight = 420,
            Child = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };

        var popup = new Popup
        {
            Child = panel,
            Placement = PlacementMode.MousePoint,
            StaysOpen = false,
            AllowsTransparency = true,
        };

        panel.Focusable = true;
        panel.KeyDown += (_, args) =>
        {
            if (args.Key != System.Windows.Input.Key.Escape) return;
            popup.IsOpen = false;
            args.Handled = true;
        };
        popup.Opened += (_, _) => _autoHide?.PushSuppress(DateTime.UtcNow);
        popup.Closed += (_, _) =>
        {
            _autoHide?.PopSuppress(DateTime.UtcNow);
            RestorePopupForeground(foregroundBefore, 0);
        };
        _ = WindowActivation.TryBringToForeground(Handle);
        popup.IsOpen = true;
        _ = panel.Focus();
    }

    private static TextBlock PanelItem(string text, Action onClick)
    {
        var item = new TextBlock
        {
            Text = text,
            Padding = new Thickness(10, 5, 10, 5),
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        item.MouseLeftButtonUp += (_, _) => onClick();
        return item;
    }

    /// <summary>
    /// 磁贴右键菜单（D-04）：菜单项与实际行为一致，不放占位项。
    /// </summary>
    /// <remarks>
    /// 主栏带 <c>WS_EX_NOACTIVATE</c>，不激活就点不到菜单外部——菜单自动收起
    /// 需要「点击外部时收起」的语义，所以打开前显式把 Dock 调到前台。
    /// 关闭后延后检查前台，仅在仍属于本弹层/主栏时归还原窗口。
    /// 失效条目（R-07）在这里给出「重新定位」入口。
    /// </remarks>
    private void OnTileContextMenuRequested(object? sender, string entryId)
    {
        if (_services is null)
        {
            return;
        }

        var entry = _services.Library.Find(entryId);

        if (entry is null)
        {
            return;
        }

        var menu = new ContextMenu
        {
            Placement = PlacementMode.MousePoint,
        };
        var foregroundBefore = WindowControl.GetForegroundWindow();
        nint menuHandle = 0;
        menu.Opened += (_, _) =>
        {
            menuHandle = (PresentationSource.FromVisual(menu) as System.Windows.Interop.HwndSource)?.Handle ?? 0;

            // 抑制压在 Opened 上、解除在 Closed 上，与启动台菜单、背景菜单同一个写法：
            // PushSuppress 只是个计数、没有超时兜底，菜单万一没能打开，Closed 就不会来，
            // 这个计数会一直挂着，Dock 从此不再自动隐藏。
            _autoHide?.PushSuppress(DateTime.UtcNow);
        };

        _ = menu.Items.Add(MenuItem("打开", () => OnTileClicked(this, entryId)));

        if (entry.Kind == AppItemKind.Folder)
        {
            _ = menu.Items.Add(MenuItem("在面板中查看内容", () => ShowFolderPanel(entry)));
        }

        _ = menu.Items.Add(new Separator());

        if (entry.IsTargetMissing)
        {
            // R-07 的界面入口：目标被删之后用户从这里把条目指到新位置。
            _ = menu.Items.Add(MenuItem("重新定位…", () => RelocateFromDialog(entry)));
        }

        _ = menu.Items.Add(MenuItem("从 Dock 移除", () => OnUnpinRequested(this, entryId)));

        menu.Closed += (_, _) =>
        {
            _autoHide?.PopSuppress(DateTime.UtcNow);
            RestorePopupForeground(foregroundBefore, menuHandle);
        };

        _ = WindowActivation.TryBringToForeground(Handle);
        menu.IsOpen = true;
    }

    /// <summary>重新定位对话框：选一个真实存在的新目标，交给维护服务。</summary>
    private void RelocateFromDialog(AppEntry entry)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"重新定位「{entry.DisplayName}」",
            CheckFileExists = false,
        };

        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        var outcome = _services!.Maintenance.Relocate(entry.Id, dialog.FileName);

        if (outcome.Result != RelocateResult.Relocated)
        {
            ShowFailureNotice(outcome.Message);
        }

        RefreshTiles();
    }

    private static MenuItem MenuItem(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>
    /// 多窗口选择列表（D-10）：列出候选窗口的标题，点哪个激活哪个。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 交互（修订 4 的焦点规则在这里落地）：
    /// <list type="bullet">
    /// <item>点一行 → 激活那一行的窗口；<b>句柄要从所属列表项取</b>，
    /// 不能只读点击来源的 Tag（来源往往是里面的文字，自己没有 Tag，
    /// 于是点了没反应也不报错——见 <see cref="WindowChooserHit"/>）；</item>
    /// <item>Enter → 激活选中行；Esc → 关闭（等同取消）；</item>
    /// <item>主栏带 <c>WS_EX_NOACTIVATE</c>，弹层要拿键盘焦点必须显式把自己调到前台；
    /// 因此打开时记下当时的前台窗口，<b>取消（Esc / 点外部）时还回去</b>，
    /// 而选中并激活成功时焦点自然留在目标应用上。</item>
    /// </list>
    /// </para>
    /// </remarks>
    private void ShowWindowChooser(IReadOnlyList<WindowCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return;
        }

        // 抑制压在弹层的 Opened 上、解除在 Closed 上（见下面），**不在打开之前**压：
        // PushSuppress 只是个计数、没有超时兜底，弹层万一没能打开，Closed 就不会来，
        // 这个计数会一直挂着，Dock 从此不再自动隐藏。四处弹层现在都是这个写法。

        // 打开之前的前台窗口：取消时还给它，避免「关掉列表后焦点不知所踪」。
        var foregroundBefore = WindowControl.GetForegroundWindow();
        var activated = false;

        var palette = ThemeService.Current?.Palette;

        var list = new ListBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
        };

        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var title = string.IsNullOrWhiteSpace(candidate.Title)
                ? $"窗口 {index + 1}"
                : candidate.Title;

            var item = new ListBoxItem
            {
                Content = title,
                Padding = new Thickness(10, 5, 10, 5),
                Tag = candidate.Handle,
            };

            list.Items.Add(item);
        }

        if (list.Items.Count > 0)
        {
            list.SelectedIndex = 0;
        }

        var panel = new PhoneBorder
        {
            Background = palette?.ResolvePanelBrush(false) ?? Brushes.DimGray,
            BorderBrush = palette?.PanelBorderBrush ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            MinWidth = 160,
        };

        panel.Child = list;

        var popup = new Popup
        {
            Child = panel,
            Placement = PlacementMode.MousePoint,
            StaysOpen = false,
            AllowsTransparency = true,
        };

        // 选中并激活某一行的窗口。返回是否真的激活成功（用于决定要不要还焦点）。
        bool Activate(nint handle)
        {
            popup.IsOpen = false;

            var outcome = LaunchService.ActivateWindow(handle);

            if (outcome.Outcome == ActivationOutcome.Activated)
            {
                activated = true;
                return true;
            }

            // 窗口已经关掉、或系统拒绝前置：不能静默，状态行上要说清楚。
            ShowFailureNotice(outcome.Message ?? "没能把那个窗口切到前台。");
            return false;
        }

        list.PreviewMouseLeftButtonUp += (_, args) =>
        {
            var handle = WindowChooserHit.Resolve(list, args.OriginalSource);

            if (handle != 0)
            {
                _ = Activate(handle);
            }
        };

        list.KeyDown += (_, args) =>
        {
            switch (args.Key)
            {
                case System.Windows.Input.Key.Enter:
                    if (list.SelectedItem is ListBoxItem { Tag: nint selected } && selected != 0)
                    {
                        _ = Activate(selected);
                    }

                    args.Handled = true;
                    break;

                case System.Windows.Input.Key.Escape:
                    popup.IsOpen = false;
                    args.Handled = true;
                    break;

                default:
                    break;
            }
        };

        popup.Opened += (_, _) => _autoHide?.PushSuppress(DateTime.UtcNow);
        popup.Closed += (_, _) =>
        {
            _autoHide?.PopSuppress(DateTime.UtcNow);

            // 取消（没激活任何窗口）时把前台还给打开前的那个窗口；
            // 激活成功时焦点已经属于目标应用，不要再抢回来。
            if (!activated)
            {
                RestorePopupForeground(foregroundBefore, 0);
            }
        };

        // 主栏不激活，弹层要收键盘就得显式取前台；随后把焦点交给列表。
        _ = WindowActivation.TryBringToForeground(Handle);
        popup.IsOpen = true;
        _ = list.Focus();
    }

    // Closed can run before the menu action or window activation completes. Defer the
    // decision, and restore only if focus is still ours. Clicking another app must win.
    private void RestorePopupForeground(nint previous, nint popupHandle)
    {
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (!WindowControl.IsAlive(previous)) return;
            var current = WindowControl.GetForegroundWindow();
            if (current == Handle || current == 0 || (popupHandle != 0 && current == popupHandle))
                _ = WindowActivation.TryBringToForeground(previous);
        }));
    }

    /// <summary>启动失败不能没有声音地过去：在状态行上亮出原因，几秒后自己恢复。</summary>
    private void ShowFailureNotice(string? message)
    {
        ShowTemporaryNotice(message);
    }

    private void ShowTemporaryNotice(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;

        _failureNoticeTimer ??= NewFailureNoticeTimer();
        _failureNoticeTimer.Stop();
        _failureNoticeTimer.Start();
    }

    private System.Windows.Threading.DispatcherTimer NewFailureNoticeTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = FailureNoticeDuration,
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            RefreshDiagnostics();
            RefreshFromSettings();
        };

        return timer;
    }

    // ---- 运行状态点（D-07） ----

    private void OnRunningChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        // 调和在线程池上触发；圆点更新回到界面线程。固定磁贴只改可见性，不重建。
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_services is null)
            {
                return;
            }

            if (_performanceSleepRequested)
            {
                // 休眠期间不重建，但要**记住有一条待办**：唤醒路径只重放标了脏的活
                // （见 DockWindow.Performance），不标的话运行条会一直停在休眠前的样子——
                // 已经关掉的程序仍在、新开的看不到，直到下次有窗口增删才纠正。
                _tilesDirty = true;
                return;
            }

            foreach (var tile in _models)
            {
                var entry = _services.Library.Find(tile.EntryId);

                tile.RunningVisibility = entry is not null && _services.Running.IsRunning(entry)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            // 右侧那一条必须跟着重建：这是「窗口一关就移除」的唯一落点。
            // 只在真的会显示时才做，免得开关关着也每次重建一遍空列表。
            if (Settings.ShowRunningApps || _running.Count > 0)
            {
                RefreshRunningTiles();
                ApplyStripMetrics();
            }
        });
    }

    // ---- 通知与刷新 ----

    /// <summary>用当前的主题与材质状态更新说明文字。</summary>
    public void RefreshDiagnostics()
    {
        var theme = ThemeService.Current;

        var surface = SurfaceMode == GlassSurfaceMode.Acrylic
            ? "亚克力背景模糊"
            : "自绘半透明面板（无背景模糊）";

        StatusText.Text = $"{surface}｜{(theme?.IsDark == true ? "深色" : "浅色")}";
        StatusText.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 按生效中的程序坞设置刷新说明文字（D-16 的可见落点之一）。
    /// </summary>
    public void RefreshFromSettings()
    {
        StatusText.Visibility = Visibility.Collapsed;
        ApplyFrameLength();
        ApplyStripMetrics();
        ApplyClock();
    }

    private void OnDockSettingsChanged(object? sender, EventArgs e)
    {
        RebuildAutoHide();
        RefreshFromSettings();
        RefreshTiles();

        // 右侧那条也必须跟着重建：「合并固定图标与运行中的应用」正是靠它生效的。
        // 少了这一步，开关拨完要等到下一次窗口增删才会看到变化，看起来就是「没生效」。
        // 与运行窗口变化那条路一样加条件，免得开关关着还每次重建一遍空列表。
        if (Settings.ShowRunningApps || _running.Count > 0)
        {
            RefreshRunningTiles();
            ApplyStripMetrics();
        }

        // 关掉系统托盘时，已经打开的面板不能留在屏幕上。
        if (!Settings.ShowSystemTray)
        {
            CloseTrayPanel();
        }

        // 位置与偏移也属于即时生效（D-16）：设置一变，已打开的 Dock 当场搬家。
        _slideOffsetPixels = 0;
        PositionOnDisplay();
    }

    /// <summary>请求宿主切换启动台；按钮点击附带按钮当前的物理屏幕矩形。</summary>
    private void RequestLauncherToggle(PixelRect? sourceBounds = null) => _toggleLauncher?.Invoke(sourceBounds);

    /// <summary>
    /// 右键程序坞空白处：弹出「任务管理器」。
    /// </summary>
    /// <remarks>
    /// 事件会从入口按钮冒泡上来，所以先看原始来源是不是落在某个按钮里——
    /// 那些按钮（以及格子）各有自己的右键菜单，不能再叠一个。
    /// 抑制自动隐藏与取前台的道理同启动台那个菜单：主栏是 <c>WS_EX_NOACTIVATE</c>，
    /// 弹层要收键盘与点选就必须显式取前台。
    /// </remarks>
    private void OnDockBackgroundRightClick(object sender, MouseButtonEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is Button)
            {
                return;
            }
        }

        e.Handled = true;

        var taskManager = new MenuItem { Header = "任务管理器" };
        taskManager.Click += (_, _) => OpenTaskManager();

        var menu = new ContextMenu
        {
            PlacementTarget = this,
            Placement = PlacementMode.Center,
        };
        menu.Items.Add(taskManager);

        menu.Opened += (_, _) => _autoHide?.PushSuppress(DateTime.UtcNow);
        menu.Closed += (_, _) => _autoHide?.PopSuppress(DateTime.UtcNow);

        _ = WindowActivation.TryBringToForeground(Handle);
        menu.IsOpen = true;
    }

    /// <summary>
    /// 打开任务管理器。
    /// </summary>
    /// <remarks>
    /// 走外壳启动而不是 <c>Process.Start</c>：任务管理器需要提升权限，
    /// 外壳会自己弹 UAC；用完整路径而不是 <c>taskmgr.exe</c>，
    /// 免得解析到别处去（PATH 与 App Paths 都可能被改）。
    /// </remarks>
    private static void OpenTaskManager()
    {
        var path = System.IO.Path.Combine(Environment.SystemDirectory, "taskmgr.exe");
        _ = ShellExecutor.Open(path, null, null);
    }

    private void OnLauncherButtonClick(object sender, RoutedEventArgs e) => RequestLauncherToggle(GetLauncherButtonPixelBounds());

    /// <summary>
    /// 右键最左侧的启动台入口：弹出「退出」。
    /// </summary>
    /// <remarks>
    /// 主栏带 <c>WS_EX_NOACTIVATE</c>，弹层要能收键盘与点选就必须显式把自己调到前台，
    /// 与文件夹面板、窗口选择列表同一套做法。菜单打开期间抑制自动隐藏，
    /// 否则指针一进菜单，Dock 就把它连同菜单一起收走了。
    /// </remarks>
    private void OnLauncherButtonRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if (ExitApplication is null)
        {
            return;
        }

        var quit = new MenuItem { Header = "退出" };
        quit.Click += (_, _) => ExitApplication();

        var menu = new ContextMenu
        {
            PlacementTarget = LauncherButton,
            Placement = PlacementMode.Bottom,
        };
        menu.Items.Add(quit);

        // 抑制与解除必须成对，所以压栈挂在 Opened 上而不是紧接创建之后：
        // PushSuppress 只是个计数，没有超时兜底（AutoHideController 里那个时间戳没被用），
        // 一旦菜单没能打开，Closed 就不会来，这个计数会一直挂着，Dock 从此不再自动隐藏。
        menu.Opened += (_, _) => _autoHide?.PushSuppress(DateTime.UtcNow);
        menu.Closed += (_, _) => _autoHide?.PopSuppress(DateTime.UtcNow);

        _ = WindowActivation.TryBringToForeground(Handle);
        menu.IsOpen = true;
    }

    /// <summary>Read the launcher button's current physical screen rectangle after Dock layout.</summary>
    public PixelRect? GetLauncherButtonPixelBounds()
    {
        if (!Settings.ShowLauncherEntry || LauncherButton.Visibility != Visibility.Visible
            || !IsVisible || !IsOpticalSurfaceVisible
            || LauncherButton.ActualWidth < 1 || LauncherButton.ActualHeight < 1) return null;

        try
        {
            var topLeft = LaunchpadTile.PointToScreen(new Point());
            var bottomRight = LaunchpadTile.PointToScreen(new Point(LaunchpadTile.ActualWidth, LaunchpadTile.ActualHeight));
            return new PixelRect((int)Math.Floor(topLeft.X), (int)Math.Floor(topLeft.Y),
                Math.Max(1, (int)Math.Ceiling(bottomRight.X - topLeft.X)),
                Math.Max(1, (int)Math.Ceiling(bottomRight.Y - topLeft.Y)));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void OnThisPcButtonClick(object sender, RoutedEventArgs e)
    {
        var opened = ShellExecutor.Open("shell:MyComputerFolder", null, null);
        if (!opened.Started)
        {
            ShowFailureNotice("此电脑没能打开。");
        }
    }

    private void OnDesktopIconsButtonClick(object sender, RoutedEventArgs e) => ToggleDesktopIcons?.Invoke();

    // ---- 时间与日期 ----

    /// <summary>
    /// 按设置启用或停掉走时定时器，并立刻写一次时间。
    /// </summary>
    /// <remarks>
    /// 定时器只在开关打开时才存在，关掉就停：一个每秒醒来的定时器
    /// 不该为了一块看不见的文字一直占用调度。
    /// </remarks>
    private void ApplyClock()
    {
        if (!Settings.ShowClock)
        {
            _clockTimer?.Stop();
            return;
        }

        UpdateClockText();

        if (_clockTimer is null)
        {
            _clockTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            _clockTimer.Tick += OnClockTick;
        }

        if (!_clockTimer.IsEnabled)
        {
            _clockTimer.Start();
        }
    }

    private void OnClockTick(object? sender, EventArgs e)
    {
        // 性能休眠或整体不可见时不走时：没人看得到的时间更新是白烧的 CPU。
        if (_performanceSleepRequested || _performanceSleeping || !IsVisible)
        {
            return;
        }

        UpdateClockText();
    }

    /// <summary>
    /// 把墙上时间写进时间与日期两行。
    /// </summary>
    /// <remarks>
    /// 固定用 24 小时制与 <c>yyyy/MM/dd</c>，不跟随区域设置：这一块的宽度是按
    /// 最宽的字形静态预留的（见 <see cref="SystemGroupWidthDip"/>），
    /// 跟随区域设置会让长短在运行时变化，预留宽度就不再成立。
    /// 取 <see cref="DateTime.Now"/> 而不是 UTC——用户要看的是墙上时钟。
    /// </remarks>
    private void UpdateClockText()
    {
        var now = DateTime.Now;
        ClockTimeText.Text = now.ToString(
            Settings.ShowClockSeconds ? "HH:mm:ss" : "HH:mm",
            CultureInfo.InvariantCulture);
        ClockDateText.Text = now.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
    }

    // ---- 系统托盘面板 ----

    private void OnTrayPanelButtonClick(object sender, RoutedEventArgs e) => ToggleTrayPanel();

    private void OnDockVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            CloseTrayPanel();
        }
    }

    /// <summary>打开或收起系统托盘面板。</summary>
    public void ToggleTrayPanel()
    {
        if (_trayPanel is { IsVisible: true })
        {
            CloseTrayPanel();
            return;
        }

        OpenTrayPanel();
    }

    /// <summary>收起系统托盘面板（如果正开着）。</summary>
    public void CloseTrayPanel()
    {
        if (_trayPanel is null || !_trayPanel.IsVisible)
        {
            return;
        }

        _trayPanel.ClosePanel();

        // 这里不再手动解除抑制：Hide() 会走 IsVisibleChanged，由那一处统一处理。
        // 而且原先这一手还够不着——面板自己收起之后 IsVisible 已是 false，
        // 本方法在开头就返回了。
    }

    private void OpenTrayPanel()
    {
        EnsureTrayPanel();
        _trayPanel!.Preferences = Settings.TrayItems;
        _trayPanel.OpenPanel();

        // 先跑一次布局，面板高度才是实测值；否则第一次打开会被当成高度 0 而不显示。
        _trayPanel.UpdateLayout();
        PositionTrayPanel();

        // 面板悬在 Dock 上方，指针一进面板就离开了 Dock 的命中区；
        // 不抑制自动隐藏的话，面板会立刻跟着 Dock 一起收起来。
        // 实际的压栈由 IsVisibleChanged 统一做（见 EnsureTrayPanel），
        // 这里不重复压——两处都压就会变成压 2 次、放 1 次，同样会卡住。
    }

    private void EnsureTrayPanel()
    {
        if (_trayPanel is not null)
        {
            return;
        }

        _trayPanel = new TrayPanelWindow();
        _trayPanel.PreferencesChanged += OnTrayPreferencesChanged;
        _trayPanel.IsTaskbarHidden = () => IsTaskbarHidden?.Invoke() ?? SystemTaskbar.IsHidden();
        _trayPanel.SetTaskbarHidden = hide => SetTaskbarHidden?.Invoke(hide) ?? SystemTaskbar.TrySetHidden(hide);

        // 刷新会改变面板行数，高度一变就要重新贴回 Dock 上方。
        _trayPanel.SizeChanged += (_, _) => PositionTrayPanel();

        // 抑制与解除由**可见性**驱动，而不是由本类的开合函数驱动。
        //
        // 面板自己也会收起：点托盘图标转发给原程序、右键退出程序、点折叠区条目，
        // 走的都是它自己的 ClosePanel()。那条路不会通知这里——Closed 不再触发，
        // CloseTrayPanel 又被 !IsVisible 挡在门外，于是抑制计数永久停在 1：
        // 自动隐藏的轮询从此直接返回，Dock 再也不收起来，会一直盖在全屏程序上。
        // IsVisibleChanged 是唯一在「所有」收起路径上都会来的信号。
        _trayPanel.IsVisibleChanged += (_, e) => SetTrayPanelAutoHideSuppressed(e.NewValue is true);
    }

    /// <summary>用户改了托盘的排序、隐藏或固定：交给宿主落盘。</summary>
    private void OnTrayPreferencesChanged(object? sender, IReadOnlyList<TrayItemPreference> preferences)
        => _ = SaveTrayPreferences?.Invoke(preferences);

    /// <summary>
    /// 面板打开期间压住自动隐藏，收起时放开；成对调用由状态量保证，
    /// 免得从其它路径（设置关掉托盘、Dock 隐藏）收起时漏掉一次。
    /// </summary>
    private void SetTrayPanelAutoHideSuppressed(bool suppressed)
    {
        if (_trayPanelSuppressingAutoHide == suppressed)
        {
            return;
        }

        _trayPanelSuppressingAutoHide = suppressed;

        if (suppressed)
        {
            _autoHide?.PushSuppress(DateTime.UtcNow);
        }
        else
        {
            _autoHide?.PopSuppress(DateTime.UtcNow);
        }
    }

    /// <summary>
    /// 把面板摆到 Dock 的正上方，右边缘与 Dock 的右边缘对齐。
    /// </summary>
    /// <remarks>
    /// 位置由 Dock 算而不是面板自己算：只有 Dock 知道自己的静止矩形与目标显示器的 DPI。
    /// 面板给的是 DIP 尺寸，这里按显示器 DPI 折算成物理像素，再夹进工作区。
    /// </remarks>
    private void PositionTrayPanel()
    {
        if (_trayPanel is null || !_trayPanel.IsVisible || _restingRect.Width <= 0)
        {
            return;
        }

        if (PrimaryDisplaySession is not { IsAvailable: true } session)
        {
            return;
        }

        var size = _trayPanel.PanelSizeDip;

        if (size.Height <= 0 || size.Width <= 0)
        {
            return;
        }

        var display = session.Display;
        var width = Math.Max(1, display.ToPixels(size.Width));
        var height = Math.Max(1, display.ToPixels(size.Height));
        var gap = Math.Max(1, display.ToPixels(TrayPanelGapDip));
        var requested = new PixelRect(_restingRect.Right - width, _restingRect.Y - gap - height, width, height);

        _ = DisplayService.MoveWindowToPixelRect(_trayPanel.PanelHandle, session.ClampWindowToWorkArea(requested));
    }

    // ---- 摆放 ----

    /// <summary>
    /// 按生效中的设置把 Dock 摆到启动时绑定的主显示器（D-15/D-23）。
    /// </summary>
    /// <remarks>
    /// 摆放走<strong>启动时 Primary Monitor 的物理像素和 DPI</strong>；旧配置中的
    /// <c>TargetDisplayId</c> 只保留数据兼容，不参与生产窗口选择。矩形交给
    /// <see cref="DisplayService.MoveWindowToPixelRect"/>。
    /// 几何在 <see cref="DockPlacementCalculator"/>（纯函数，可离线单测）。
    /// </remarks>
    public bool SuppressPassiveTriggersWhenFullscreen { get; set; } = true;
    public void RefreshDisplayPlacement()
    {
        ApplyFrameLength();
        ApplyStripMetrics();
        PositionOnDisplay();
    }

    public void StopForPrimaryDisplayChange()
    {
        _frameTimer?.Stop();
        Hide();
    }

    public void ResumeAfterPrimaryDisplayChange(bool show)
    {
        RebuildAutoHide();
        RefreshDisplayPlacement();
        if (show && !_performanceSleepRequested) Show();
    }

    public void PositionOnDisplay()
    {
        var handle = Handle;

        if (handle == 0)
        {
            return;
        }

        var display = CurrentPrimaryDisplay;
        if (display is null)
        {
            return;
        }

        // 用了 SizeToContent，最终尺寸要等布局跑完才知道。
        var widthDips = ActualWidth > 0 ? ActualWidth : DesiredSize.Width;
        var heightDips = ActualHeight > 0 ? ActualHeight : DesiredSize.Height;

        if (widthDips <= 0 || heightDips <= 0)
        {
            return;
        }

        // 极端缩放下取整可能得 0；窗口至少要有 1 物理像素。
        var widthPixels = Math.Max(1, display.ToPixels(widthDips));
        var heightPixels = Math.Max(1, display.ToPixels(heightDips));

        _restingRect = DockPlacementCalculator.Calculate(
            display.WorkArea,
            Settings.Placement,
            Settings.OffsetX,
            Settings.OffsetY,
            widthPixels,
            heightPixels,
            display.Dpi);

        if (PrimaryDisplaySession is { IsAvailable: true } session)
            _restingRect = session.ClampWindowToWorkArea(_restingRect);

        _fullyHiddenOffsetPixels = SlideOutDistancePixels(_restingRect, display.Bounds, Settings.Placement);

        ApplySlide(_restingRect, _slideOffsetPixels);

        // Dock 挪了位置，悬在它上方的面板要跟着挪。
        PositionTrayPanel();
    }

    /// <summary>保持 HWND 在主屏工作区内，只在窗口剪裁范围内滑动内容与共享面板几何。</summary>
    private void ApplySlide(PixelRect restingRect, double slideOffsetPixels)
    {
        var slidePixels = (int)Math.Round(slideOffsetPixels * SlideOutDirection(), MidpointRounding.AwayFromZero);
        var concealed = restingRect.Height > 0 && Math.Abs(slidePixels) >= restingRect.Height;
        // HWND equality describes layout, not the pixels painted while hidden.
        // Retire old glass only once the entire panel is clipped, so every reveal
        // prepares current content without disturbing the visible hide animation.
        if (concealed && !_panelWasConcealed) GlassSurface.RetireConcealedMaterial();
        _panelWasConcealed = concealed;
        var backdropActive = IsOpticalSurfaceVisible;
        if (backdropActive && !_backdropWasActive) GlassSurface.RequestBackdropRefresh(revealing: true);
        _backdropWasActive = backdropActive;
        if (PrimaryDisplaySession is { } session)
        {
            if (!session.IsAvailable) return;
            var fixedWindow = session.ClampWindowToWorkArea(restingRect);
            if (DisplayService.GetWindowPixelBounds(Handle) != fixedWindow)
                _ = DisplayService.MoveWindowToPixelRect(Handle, fixedWindow);
            var scale = Math.Max(.01, session.Display.ScaleFactor);
            var visualOffsetDip = slidePixels / scale;
            DockSlideTransform.Y = visualOffsetDip;
            _slidePanelBoundsDip = new Rect(0, visualOffsetDip, ActualWidth, ActualHeight);
            if (ActualWidth > 0 && ActualHeight > 0)
            {
                // Match the launcher: a fixed desktop texture with a moving GPU lens.
                // Only Content translates. New capture frames keep the same coordinates
                // throughout the slide and therefore remain consumable during motion.
                var hiddenOffsetDip = _fullyHiddenOffsetPixels * SlideOutDirection() / scale;
                GlassSurface.SetPanelShape(_slidePanelBoundsDip.Value, PanelCornerRadius.TopLeft);
                GlassSurface.SetContainerTransitionBounds(
                    new Rect(0, Math.Min(0, hiddenOffsetDip), ActualWidth,
                        ActualHeight + Math.Abs(hiddenOffsetDip)));
                SetSharedPanelClip(_slidePanelBoundsDip.Value, PanelCornerRadius.TopLeft);
            }
            var shadowStrength = Math.Clamp(1 - Math.Abs(visualOffsetDip) / Math.Max(1, ActualHeight), 0, 1);
            SetTransitionShadowShape(Math.Abs(visualOffsetDip) < .01
                    ? null
                    : _slidePanelBoundsDip,
                PanelCornerRadius.TopLeft, shadowStrength);
            return;
        }

        // Unbound design/test windows keep their legacy placement behavior. AppHost binds
        // every production GlassWindow to one immutable PrimaryDisplaySession.
        DockSlideTransform.Y = 0;
        SetTransitionShadowShape(null, 0);
        var rect = restingRect with { Y = restingRect.Y + slidePixels };
        _ = DisplayService.MoveWindowToPixelRect(Handle, rect);
    }

    /// <summary>内容滑出方向：底部 Dock 向下 +1，顶部 Dock 向上 −1。</summary>
    private int SlideOutDirection()
        => Settings.Placement == DockPlacement.TopCenter ? -1 : 1;

    /// <summary>内容完全隐藏所需的物理像素偏移，窗口 HWND 本身始终留在主屏内。</summary>
    private static int SlideOutDistancePixels(PixelRect restingRect, PixelRect displayBounds, DockPlacement placement)
    {
        var distanceToDisplayEdge = placement == DockPlacement.TopCenter
            ? restingRect.Bottom - displayBounds.Y
            : displayBounds.Bottom - restingRect.Y;

        // Dock 靠工作区边缘停靠，工作区与显示器边缘之间可能有任务栏；再多移两像素，
        // 避免物理像素取整或窗口表面边界留下细线。
        return Math.Max(0, distanceToDisplayEdge) + 2;
    }

    // ---- 自动隐藏（D-19） ----

    /// <summary>按当前设置重建状态机。设置可随时改，状态机不可变所以整体换。</summary>
    private void RebuildAutoHide()
    {
        var settings = Settings;

        // 新建状态机会把抑制计数清零，而此刻可能正有面板或弹出物开着——
        // 本方法的第一句调用来自 OnDockSettingsChanged，也就是「打开面板时改任何设置」
        // 都会走到这里。不把余额搬过去，Dock 就会从打开的面板底下收走（探针实测：计数 1 → 0）。
        var carriedSuppressions = _autoHide?.SuppressCount ?? 0;

        _autoHide = new AutoHideController(settings.AutoHideLeaveDelayMs);

        for (var index = 0; index < carriedSuppressions; index++)
        {
            _autoHide.PushSuppress(DateTime.UtcNow);
        }

        _autoHide.ForceShown(DateTime.UtcNow);
        _slideOffsetPixels = 0;

        if (_frameTimer is null)
        {
            // Capture completion/icon work can keep Normal queued while the panel
            // is concealed. Poll/slide admission must still advance to its first
            // visible pixel; settled idle remains 100 ms and game sleep stops it.
            _frameTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Send)
            {
                Interval = FrameInterval,
            };
            _frameTimer.Tick += (_, _) => OnAutoHideFrame();
        }

        _frameTimer.IsEnabled = !_performanceSleepRequested;
        _frameTimer.Interval = TimeSpan.FromMilliseconds(100);
    }

    /// <summary>
    /// 公共帧：探测指针 → 状态机 → 滑动插值。
    /// </summary>
    /// <remarks>
    /// 全部判定用<strong>缓存的静止矩形</strong>与显示枚举，不碰 WPF 可视树；
    /// 16 ms 一次的成本只有一次光标查询与少量算术。
    /// </remarks>
    private void OnAutoHideFrame()
    {
        if (_performanceSleepRequested) { AdvancePerformanceSleep(); return; }
        if (_autoHide is null || _services is null || _restingRect.Width == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var cursor = DisplayService.GetCursorPixelPoint();
        var fullscreen = FullscreenDetector.IsFullscreenActive();
        var covered = ForegroundCoverage.IsCovered(_restingRect);
        var suppressed = fullscreen || covered;
        var pointerOverDock = cursor is { } currentPoint
            && Inflate(GetVisibleDockRect(), 2).Contains(currentPoint.X, currentPoint.Y);

        var pointerOverDockPosition = cursor is { } position
            && Inflate(_restingRect, 2).Contains(position.X, position.Y);
        UpdateAutoHideState(now, fullscreen, covered, pointerOverDockPosition, pointerOverDock);

        // 滑动插值：向目标滑出量靠近。
        var target = _autoHide.State == AutoHideState.Hidden
            ? (double)_fullyHiddenOffsetPixels
            : 0.0;

        // Admit the reveal intent before preparation starts the native owner.
        // Waiting for optical visibility lets a foreground-policy tick put that
        // new owner back to sleep while the panel is still crossing the edge.
        NotifyPerformanceSurfaceVisibility();

        // Prepare a complete material before the first pixels slide back into view.
        if (target == 0 && _backgroundCaptureSleeping) SetBackgroundCaptureSleeping(false);
        else if (target == 0) PrepareConcealedReveal();

        if (ThemeService.Current?.AnimationsEnabled == false)
        {
            _slideOffsetPixels = target;
            ApplySlide(_restingRect,target);
            NotifyPerformanceSurfaceVisibility();
            return;
        }
        if (Math.Abs(_slideOffsetPixels - target) > 0.5)
        {
            _slideOffsetPixels += (target - _slideOffsetPixels) * 0.25;

            if (Math.Abs(_slideOffsetPixels - target) <= 0.5)
            {
                _slideOffsetPixels = target;
            }

            ApplySlide(_restingRect, _slideOffsetPixels);
        }
        else if (_slideOffsetPixels != target)
        {
            _slideOffsetPixels = target;
            ApplySlide(_restingRect, _slideOffsetPixels);
        }

        if (_frameTimer is not null)
            _frameTimer.Interval = !_backgroundCaptureSleeping && (suppressed || _autoHide.Suppressed)
                || Math.Abs(_slideOffsetPixels - target) > 0.5
                ? FrameInterval
                : TimeSpan.FromMilliseconds(100);
        NotifyPerformanceSurfaceVisibility();
    }

    private void UpdateAutoHideState(DateTime now, bool fullscreen, bool covered,
        bool pointerOverDockPosition, bool pointerOverVisibleDock)
    {
        if (_autoHide is null) return;
        if (_launcherContainerActive)
        {
            _edgeRevealedDuringSuppression = false;
            _autoHide.ForceShown(now);
        }
        else if (fullscreen || covered)
        {
            if (_revealLocked)
            {
                _edgeRevealedDuringSuppression = false;
                _autoHide.ForceHide(now);
            }
            else if (_autoHide.State == AutoHideState.Hidden)
            {
                if (PassiveRevealPolicy.CanRevealDuringSuppression(pointerOverDockPosition, _revealLocked)
                    && _autoHide.TryReveal(false, now))
                    _edgeRevealedDuringSuppression = true;
            }
            else if (_edgeRevealedDuringSuppression)
            {
                if (pointerOverVisibleDock) _autoHide.PointerEnter(now);
                else _autoHide.PointerLeave(now);
                _autoHide.Poll(now);
            }
            else
                _autoHide.ForceHide(now);
        }
        else
        {
            _edgeRevealedDuringSuppression = false;
            _autoHide.ForceShown(now);
        }

        if (_autoHide.State == AutoHideState.Hidden)
            _edgeRevealedDuringSuppression = false;
    }

    private PixelRect GetVisibleDockRect()
    {
        var slidePixels = (int)Math.Round(_slideOffsetPixels * SlideOutDirection(), MidpointRounding.AwayFromZero);
        var translated = _restingRect with { Y = _restingRect.Y + slidePixels };
        if (PrimaryDisplaySession is null) return translated;
        var left = Math.Max(_restingRect.X, translated.X);
        var top = Math.Max(_restingRect.Y, translated.Y);
        var right = Math.Min(_restingRect.Right, translated.Right);
        var bottom = Math.Min(_restingRect.Bottom, translated.Bottom);
        return right <= left || bottom <= top
            ? new PixelRect(_restingRect.X, _restingRect.Y, 0, 0)
            : new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>切换本次运行期间的遮挡唤出锁；不影响未被覆盖的 Dock。</summary>
    public void ToggleRevealLock()
    {
        _revealLocked = !_revealLocked;
        if (_performanceSleepRequested) return;
        if (_frameTimer is not null)
        {
            _frameTimer.Interval = FrameInterval;
            _frameTimer.IsEnabled = true;
        }
        OnAutoHideFrame();
    }

    private static PixelRect Inflate(PixelRect rect, int by)
        => new(rect.X - by, rect.Y - by, rect.Width + by * 2, rect.Height + by * 2);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        RefreshDiagnostics();

        // 必须在句柄建立之后改扩展样式：点击本窗口时不得把前台让给别人。
        // 弹出物要取焦点时显式调 WindowActivation.TryBringToForeground。
        WindowStyles.MakeNoActivate(Handle);
    }

    protected override void OnClosed(EventArgs e)
    {
        _frameTimer?.Stop();
        _failureNoticeTimer?.Stop();
        _clockTimer?.Stop();
        IsVisibleChanged -= OnDockVisibilityChanged;

        RunningTileStripHost.MouseMove -= OnRunningStripMouseMove;
        RunningTileStripHost.MouseLeave -= OnRunningStripMouseLeave;
        _runningStrip.TileClicked -= OnRunningTileClicked;

        if (_runningPreviewTimer is not null)
        {
            _runningPreviewTimer.Stop();
            _runningPreviewTimer.Tick -= OnRunningPreviewTick;
            _runningPreviewTimer = null;
        }

        CloseRunningPreview();

        // 面板是本窗口的子窗口：Dock 关掉就必须真的关掉它，
        // 否则屏幕上会留下一个没有 Dock 可依附的悬空面板。
        if (_trayPanel is not null)
        {
            _trayPanel.PreferencesChanged -= OnTrayPreferencesChanged;
            _trayPanel.Close();
            _trayPanel = null;
        }

        if (_dockSettings is not null)
        {
            _dockSettings.Changed -= OnDockSettingsChanged;
        }

        if (_services is not null)
        {
            _services.Running.Changed -= OnRunningChanged;
        }

        base.OnClosed(e);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        // 窗口尺寸（内容）变化后按设置重新落位；SizeToContent 的首次布局也会走到这里。
        PositionOnDisplay();
    }
}
