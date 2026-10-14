using System.Runtime.InteropServices;
using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform.Shell;

/// <summary>
/// 系统任务栏的显隐，对应系统控制区里的「隐藏系统任务栏」。
/// </summary>
/// <remarks>
/// 分两层，两层解决的是不同的问题：
/// <list type="bullet">
/// <item><b>自动隐藏</b>（<c>ABM_SETSTATE</c>，有文档记载）让外壳把任务栏占的那一条
/// 还给窗口——不这么做，任务栏只是看不见了，最大化窗口仍会在底部留一条空白；</item>
/// <item><b>隐藏窗口</b>（<c>ShowWindow(SW_HIDE)</c>）让它不能被重新拉起来。
/// 只设自动隐藏是不够的：鼠标顶到屏幕边缘，任务栏照样会滑出来。</item>
/// </list>
///
/// <para>
/// <b>这里确实动了任务栏窗口，与仓库原先「Never hides, moves or restarts the system taskbar」
/// 的立场相反。</b>改立场的原因是产品要求：开关打开后，软件运行期间任务栏不允许被重新拉起。
/// 自动隐藏做不到这一点，而除了隐藏窗口没有别的办法。
/// 代价是任务栏与外壳自己的状态会短暂脱节（系统设置里仍显示为已开启自动隐藏），
/// 因此<strong>恢复必须走同一条路</strong>：把窗口放回来，并把自动隐藏还原成改动前的值。
/// 还原的责任在调用方（<c>TaskbarHider</c>），包括进程被结束时由守护进程代劳。
/// </para>
/// </remarks>
public static class SystemTaskbar
{
    /// <summary>主任务栏的窗口类名。</summary>
    private const string PrimaryTaskbarClass = "Shell_TrayWnd";

    /// <summary>副显示器上的任务栏类名。</summary>
    private const string SecondaryTaskbarClass = "Shell_SecondaryTrayWnd";

    /// <summary>
    /// 读任务栏此刻是否设为自动隐藏。
    /// </summary>
    /// <remarks>
    /// 复用 <see cref="FullscreenDetector.IsTaskbarAutoHidden"/>：它已经在做同一件事，
    /// 而全屏判定也依赖这条结论，两处必须是同一个读法。
    /// </remarks>
    public static bool IsAutoHidden() => FullscreenDetector.IsTaskbarAutoHidden();

    /// <summary>
    /// 设置任务栏是否自动隐藏；写入后回读一次，只有确实生效才算成功。
    /// </summary>
    public static bool TrySetAutoHidden(bool autoHide)
    {
        try
        {
            var data = new Shell32.AppBarData
            {
                Size = (uint)Marshal.SizeOf<Shell32.AppBarData>(),
                Window = User32.FindWindow(PrimaryTaskbarClass, null),
                Parameter = (nint)(long)(autoHide ? Shell32.AbsAutoHide : Shell32.AbsAlwaysOnTop),
            };

            _ = Shell32.SHAppBarMessage(Shell32.AbmSetState, ref data);
            return IsAutoHidden() == autoHide;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>任务栏窗口此刻是不是被隐藏了（不是自动隐藏：自动隐藏的窗口仍然是可见窗口）。</summary>
    public static bool IsHidden()
    {
        try
        {
            var primary = User32.FindWindow(PrimaryTaskbarClass, null);
            return primary != 0 && !User32.IsWindowVisible(primary);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// 给定的这批任务栏窗口是不是都还藏着。
    /// </summary>
    /// <remarks>
    /// 给「每隔几百毫秒确认一次」用的重载：它只对调用方已经拿到的句柄做两次廉价查询，
    /// 不再去 <c>FindWindow</c> 枚举一遍窗口。句柄失效（外壳重启、显示器增减）时返回
    /// <c>false</c>，调用方据此重新取一遍句柄并再隐藏一次。
    /// </remarks>
    public static bool IsHidden(IReadOnlyList<nint> taskbars)
    {
        ArgumentNullException.ThrowIfNull(taskbars);

        if (taskbars.Count == 0)
        {
            return false;
        }

        try
        {
            foreach (var taskbar in taskbars)
            {
                if (!User32.IsWindow(taskbar) || User32.IsWindowVisible(taskbar))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// 隐藏或显示全部任务栏窗口（含副显示器上的）。
    /// </summary>
    /// <remarks>
    /// 返回是否至少作用到了一个任务栏窗口。一个都没有通常意味着外壳没有在跑，
    /// 调用方据此知道「这次动作没有意义」，而不是以为已经生效。
    /// </remarks>
    public static bool TrySetHidden(bool hidden)
    {
        try
        {
            var taskbars = FindTaskbars();

            if (taskbars.Count == 0)
            {
                return false;
            }

            foreach (var taskbar in taskbars)
            {
                _ = User32.ShowWindow(taskbar, hidden ? User32.SwHide : User32.SwShow);
            }

            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>取当前所有任务栏窗口：主任务栏，以及每块副显示器上的那条。</summary>
    public static IReadOnlyList<nint> FindTaskbars()
    {
        var taskbars = new List<nint>(2);
        var primary = User32.FindWindow(PrimaryTaskbarClass, null);

        if (primary != 0)
        {
            taskbars.Add(primary);
        }

        // 副显示器的任务栏没有名字，只能沿同一个类名往下数。
        nint secondary = 0;

        while (true)
        {
            secondary = User32.FindWindowEx(0, secondary, SecondaryTaskbarClass, null);

            if (secondary == 0)
            {
                break;
            }

            taskbars.Add(secondary);
        }

        return taskbars;
    }
}
