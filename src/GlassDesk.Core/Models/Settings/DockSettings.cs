namespace GlassDesk.Core.Models.Settings;

/// <summary>程序坞停在哪里、以及它如何呈现。</summary>
public enum DockPlacement
{
    BottomCenter = 0,
    BottomLeft = 1,
    BottomRight = 2,
    TopCenter = 3,
}

public sealed class DockSettings
{
    public const double FixedIconSize = 48;
    public const double FixedIconSpacing = 8;
    public const double FixedMagnificationPeak = 1.15;
    public const double MinimumFrameLengthDip = 180;
    public const double MaximumFrameLengthDip = 10000;
    public const double MinimumSizeScale = 2d / 3;
    public const double MaximumSizeScale = 1.5;

    /// <summary>整体尺寸倍率，同时调整图标、间距与栏厚度。旧配置默认保持原尺寸。</summary>
    public double SizeScale { get; set; } = 1;

    /// <summary>内置入口固定在应用左侧；旧配置默认显示全部入口。</summary>
    public bool ShowLauncherEntry { get; set; } = true;
    public bool ShowThisPcEntry { get; set; } = true;
    public bool ShowDesktopIconsEntry { get; set; } = true;

    /// <summary>
    /// Dock 玻璃外框的横向长度，单位为 DIP。零表示继续按内容自动调整，
    /// 正数表示固定外框长度；图标尺寸与 Dock 厚度不受此值影响。
    /// </summary>
    public double FrameLengthDip { get; set; }

    /// <summary>图标边长，单位为 DIP。</summary>
    public double IconSize { get; set; } = FixedIconSize;

    /// <summary>图标之间的间隙，单位为 DIP。</summary>
    public double IconSpacing { get; set; } = FixedIconSpacing;

    /// <summary>光标处的放大峰值，以 <see cref="IconSize"/> 为基准的倍数。</summary>
    /// <remarks>
    /// 默认 1.35 是 D-21 定下的产品值，测试与验收都在守它
    /// （<c>SettingsStoreTests</c>、<c>DockWindowStyleTests</c>、<c>Phase4Acceptance</c>）。
    ///
    /// <para>
    /// <b>已知冲突（待决）</b>：渲染点目前读的是 <see cref="FixedMagnificationPeak"/>（1.15），
    /// 不读这个属性——于是「设置里记的 1.35」与「实际放大 1.15」是两个数，
    /// 而设置页写的是常量，碰一下 Dock 控件磁盘上的值就从 1.35 变成 1.15。
    /// 权威值应为 1.35（上面那些断言），所以该改的是渲染点；但那是可见的外观变化，
    /// 需要先确认再动，不能顺手改。
    /// </para>
    /// </remarks>
    public double MagnificationPeak { get; set; } = 1.35;

    public DockPlacement Placement { get; set; } = DockPlacement.BottomCenter;

    /// <summary>相对停靠锚点的水平微调量，单位为 DIP。</summary>
    public double OffsetX { get; set; }

    /// <summary>相对停靠锚点的垂直微调量，单位为 DIP。</summary>
    public double OffsetY { get; set; }

    /// <summary>
    /// 自动隐藏的程序坞收起前的缓冲时间。没有它，指针划过边缘时程序坞
    /// 会来回闪烁；方案要求约 300 ms。
    /// </summary>
    public int AutoHideLeaveDelayMs { get; set; } = 300;

    /// <summary>
    /// 当其他窗口盖住程序坞区域时，鼠标移入程序坞原位置属于明确悬停操作，仍会唤出。
    /// 该设置原字段保留兼容既有配置；显式边缘唤出锁仍拥有最高优先级。
    /// </summary>
    public bool BlockRevealWhenCovered { get; set; } = true;

    /// <summary>
    /// 旧版本 Dock 显示器选择器的兼容字段。生产 Dock 始终使用进程启动时的 Primary Monitor，
    /// 不读取此字段进行放置。
    /// </summary>
    public string? TargetDisplayId { get; set; }

    /// <summary>
    /// 在 Dock 右侧显示打开的应用：每个有顶层窗口的应用一个磁贴，同一应用的窗口合成一个。
    /// </summary>
    /// <remarks>
    /// 默认关闭。这个开关会改变 Dock 上出现哪些磁贴，属于「用户主动要求才启用」的外观变化；
    /// 与它配套的 <see cref="MergePinnedWithRunning"/> 只在它打开时才有意义。
    /// 只统计顶层窗口：只缩在通知区、没有任何窗口的进程不显示，
    /// 这与运行状态点表达的是同一件事——「有窗口在」，不是「进程在」。
    /// </remarks>
    public bool ShowRunningApps { get; set; }

    /// <summary>
    /// 合并固定图标与运行中的应用：已固定的应用不在右侧重复显示，改为通过固定图标恢复与预览。
    /// </summary>
    /// <remarks>
    /// 默认开启：不合并意味着同一个应用在 Dock 上出现两次，那是需要被解释的行为，
    /// 不该是新用户看到的第一印象。
    /// </remarks>
    public bool MergePinnedWithRunning { get; set; } = true;

    /// <summary>
    /// 在 Dock 最右侧显示系统本地时间与日期。
    /// </summary>
    /// <remarks>
    /// 时间取系统本地时区，不用 <c>DateTimeOffset.UtcNow</c> 显示——用户看的是墙上时钟。
    /// </remarks>
    public bool ShowClock { get; set; }

    /// <summary>
    /// 时间精确到秒（<c>HH:mm:ss</c>）；关掉时只到分（<c>HH:mm</c>）。
    /// </summary>
    /// <remarks>
    /// 默认开启。秒针让时间看起来是"活的"，代价是每秒一次重绘——
    /// 关掉它可以让 Dock 在没人看的时候更安静。只在 <see cref="ShowClock"/> 打开时有意义。
    /// </remarks>
    public bool ShowClockSeconds { get; set; } = true;

    /// <summary>
    /// Dock 右侧的系统托盘面板：把 Windows 通知区域的图标收纳进一个可展开的面板。
    /// </summary>
    /// <remarks>
    /// 只做收纳与转发，不改变 Windows 通知区域自己的内容与顺序；
    /// 关掉这个开关时 Windows 托盘原样保留。
    /// </remarks>
    public bool ShowSystemTray { get; set; }

    /// <summary>
    /// 用户对托盘图标做的排序、隐藏与固定。
    /// </summary>
    /// <remarks>
    /// 空列表表示「全部显示、按 Windows 通知区域的原始顺序」——这是新用户看到的默认状态，
    /// 因此它不能是非空初始值。未出现在列表里的图标一律按原始顺序排在已列出的图标之后。
    /// </remarks>
    public List<TrayItemPreference> TrayItems { get; set; } = [];
}
