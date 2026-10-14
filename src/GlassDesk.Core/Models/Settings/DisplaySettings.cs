namespace GlassDesk.Core.Models.Settings;

/// <summary>与显示器和 DPI 相关的行为。</summary>
public sealed class DisplaySettings
{
    /// <summary>
    /// 旧版本 Dock 显示器选择器的兼容字段。Chapter 11 起生产 Dock 永远使用进程启动时
    /// Windows 选定的 Primary Monitor，此字段不再参与窗口放置。
    /// </summary>
    public string? DockDisplayId { get; set; }

    /// <summary>
    /// 旧版本跨显示器跟随设置的兼容字段。生产启动台始终使用进程启动时绑定的 Primary Monitor。
    /// </summary>
    public bool LauncherFollowsPointer { get; set; } = true;

    /// <summary>
    /// 前台运行全屏应用时，抑制热角、边缘触发与鼠标侧键触发，
    /// 使它们不会打断游戏或视频。
    /// </summary>
    public bool SuppressTriggersWhenFullscreen { get; set; } = true;
}

/// <summary>
/// 桌面图标与任务栏。
/// </summary>
/// <remarks>
/// 这里的一切默认都是「不打扰 Windows」：除非用户主动打开开关，
/// 否则不得修改任务栏；边缘触发拦截属于实验性功能，默认保持关闭。
/// </remarks>
public sealed class DesktopTaskbarSettings
{
    public HotkeyDefinition ToggleDesktopIcons { get; set; } = new();

    /// <summary>
    /// 用户的意图：在 GlassDesk 运行期间隐藏系统任务栏。
    /// </summary>
    /// <remarks>
    /// 默认关闭。存的是<strong>意图</strong>而不是「此刻是否隐藏着」——
    /// 任务栏的隐藏只在进程活着的时候成立：退出、被结束进程、或用户关掉这个开关，
    /// 都必须把任务栏放回来（见 <c>TaskbarHider</c> 与它拉起的守护进程）。
    /// 进程下次启动时按这个意图重新隐藏。
    /// </remarks>
    public bool HideTaskbar { get; set; }

    /// <summary>
    /// 隐藏任务栏之前，Windows 原本的自动隐藏设置。
    /// </summary>
    /// <remarks>
    /// 隐藏时要顺手打开自动隐藏，让外壳把任务栏占的那一条还给窗口；
    /// 那么恢复时必须把这一位还原成<strong>改动前</strong>的值，
    /// 而不是一律关掉——用户本来就用着自动隐藏的话，关掉它就是改了他的设置。
    ///
    /// <para>
    /// 存在设置里而不是只放内存，是因为进程可能被直接结束：
    /// 那时只有守护进程读得到这个值，它靠命令行参数拿到。
    /// </para>
    /// </remarks>
    public bool TaskbarAutoHideBeforeHide { get; set; }

    internal void Normalize()
    {
        ToggleDesktopIcons ??= new HotkeyDefinition();
    }
}
