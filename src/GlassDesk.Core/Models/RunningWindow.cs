namespace GlassDesk.Core.Models;

/// <summary>
/// 参与「Dock 右侧打开的应用」分组的顶层窗口投影。
/// </summary>
/// <remarks>
/// 刻意不直接用 Platform 的窗口记录：Core 被 Platform 引用，不能反向依赖它。
/// 界面层把枚举结果投影成这个记录再交给分组算法，分组因此是纯函数、可单测。
/// </remarks>
/// <param name="Handle">窗口句柄。</param>
/// <param name="Title">窗口标题。</param>
/// <param name="ProcessId">窗口所属进程。</param>
/// <param name="IsMinimized">窗口是否已最小化。</param>
/// <param name="ImagePath">进程可执行文件路径；取不到时为空。</param>
/// <param name="AppId">应用身份（<c>System.AppUserModel.ID</c>）；没有时为空。</param>
public sealed record RunningWindow(
    nint Handle,
    string Title,
    uint ProcessId,
    bool IsMinimized,
    string? ImagePath,
    string? AppId);

/// <summary>
/// 同一个应用的一组顶层窗口，对应 Dock 右侧的一个磁贴。
/// </summary>
/// <param name="Key">分组键，见 <see cref="RunningAppPolicy.ComposeKey"/>。</param>
/// <param name="AppId">该组的应用身份；没有时为空。</param>
/// <param name="ImagePath">该组的可执行文件路径；取不到时为空。</param>
/// <param name="Windows">该组的窗口，按枚举顺序（即 z 序，最近的在前）。</param>
public sealed record RunningAppGroup(
    string Key,
    string? AppId,
    string? ImagePath,
    IReadOnlyList<RunningWindow> Windows)
{
    /// <summary>这个应用是否只有一个窗口（点击时不必再弹选择列表）。</summary>
    public bool HasSingleWindow => Windows.Count == 1;

    /// <summary>该组里有几个窗口是最小化的。</summary>
    public int MinimizedCount
    {
        get
        {
            var count = 0;

            foreach (var window in Windows)
            {
                if (window.IsMinimized)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
