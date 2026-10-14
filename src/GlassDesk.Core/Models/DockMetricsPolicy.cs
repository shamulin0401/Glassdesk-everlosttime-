using GlassDesk.Core.Models.Settings;

namespace GlassDesk.Core.Models;

/// <summary>
/// Dock 横向尺寸的共享算法。
/// </summary>
/// <remarks>
/// 程序坞用它算磁贴条的内容封顶与外框最小长度，设置窗口用它算外框长度滑块的下限。
/// 两处必须得出同一个数字：滑块允许的长度如果比程序坞认可的下限小，
/// 选了那个长度就会把图标或右侧的时间挤出玻璃面板。所以公式只写在这里一份。
/// </remarks>
public static class DockMetricsPolicy
{
    /// <summary>每个内置入口占用的宽度（DIP）：图标 48 加右侧间距 8。</summary>
    public const double BuiltinEntryWidth = 56;

    /// <summary>托盘展开按钮的边长（DIP）。</summary>
    public const double TrayButtonWidthDip = 26;

    /// <summary>
    /// 时间日期块预留的宽度（DIP）。
    /// </summary>
    /// <remarks>
    /// 按最宽的字形估：日期「2026/10/14」在 10.5 DIP 下约 56 DIP 宽。
    /// 宁可多留一点，也不要在固定外框下把时间裁掉。
    /// </remarks>
    public const double ClockWidthDip = 62;

    /// <summary>
    /// 精确到秒时时间块需要额外预留的宽度（DIP）：多出 <c>:ss</c> 三个字形。
    /// </summary>
    /// <remarks>
    /// 带秒以后 <c>HH:mm:ss</c> 比日期那一行还宽，成为这个块的最宽内容，
    /// 所以必须跟着加宽——外框宽度是静态预留的，留窄了时间会被裁掉。
    /// </remarks>
    public const double ClockSecondsExtraWidthDip = 28;

    /// <summary>按设置算出时间日期块要预留的宽度（DIP，未乘尺寸倍率）。</summary>
    public static double ClockWidthFor(DockSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return ClockWidthDip + (settings.ShowClockSeconds ? ClockSecondsExtraWidthDip : 0);
    }

    /// <summary>系统组内两个子项之间的间隙（DIP）。</summary>
    public const double SystemGroupGapDip = 8;

    /// <summary>内置入口之外的面板左右留白与内边距合计（DIP）。</summary>
    public const double ChromeWidthDip = 22;

    /// <summary>系统组底板的内边距与外边距合计（DIP）：左右各 6，加组外边距 8。</summary>
    public const double SystemGroupChromeDip = 20;

    /// <summary>内置入口合计占用的宽度（DIP，未乘尺寸倍率）。</summary>
    public static double BuiltinEntryWidthDip(DockSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var count = (settings.ShowLauncherEntry ? 1 : 0)
            + (settings.ShowThisPcEntry ? 1 : 0)
            + (settings.ShowDesktopIconsEntry ? 1 : 0);

        return BuiltinEntryWidth * count;
    }

    /// <summary>右侧系统组合计占用的宽度（DIP，未乘尺寸倍率）；两个子项都关掉时为 0。</summary>
    public static double SystemGroupWidthDip(DockSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var content = 0d;

        if (settings.ShowSystemTray)
        {
            content += TrayButtonWidthDip;
        }

        if (settings.ShowClock)
        {
            content += (content > 0 ? SystemGroupGapDip : 0) + ClockWidthFor(settings);
        }

        return content > 0 ? content + SystemGroupChromeDip : 0;
    }

    /// <summary>
    /// 外框长度的下限：内置入口、右侧系统组与面板留白都要放得下，且不小于产品下限。
    /// </summary>
    /// <remarks>
    /// <b>刻意不含「打开的应用」那一条。</b>它的宽度取决于此刻开着几个程序，
    /// 不是一个常量，放进静态下限只会让滑块的可用范围随运行状态漂移。
    /// 空间不够时由程序坞把预算切开、让那一条自己横向滚动，而不是抬高外框下限。
    /// </remarks>
    public static double MinimumFrameLengthDip(DockSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var scale = double.IsFinite(settings.SizeScale)
            ? Math.Clamp(settings.SizeScale, DockSettings.MinimumSizeScale, DockSettings.MaximumSizeScale)
            : 1;

        return Math.Max(
            DockSettings.MinimumFrameLengthDip,
            BuiltinEntryWidthDip(settings) + SystemGroupWidthDip(settings) + ChromeWidthDip) * scale;
    }
}
