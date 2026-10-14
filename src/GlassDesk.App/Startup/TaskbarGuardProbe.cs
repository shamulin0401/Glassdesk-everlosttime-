using System.Diagnostics;
using System.Globalization;
using GlassDesk.Platform.Shell;

namespace GlassDesk.App.Startup;

/// <summary>
/// 任务栏守护进程：等父进程结束后，把任务栏放回来。
/// </summary>
/// <remarks>
/// 它存在只为一件事：<b>进程被直接结束时，进程自己没有任何机会执行清理代码。</b>
/// 父进程留不住代码，就把这件事托给一个能比它活得久的进程——守护进程持有父进程的句柄，
/// 父进程一消失（正常退出、崩溃、被任务管理器结束，都一样）它就恢复任务栏，
/// 然后自己退出。
///
/// <para>
/// 它刻意不创建任何 WPF 界面、不取单实例互斥体、不碰用户数据目录：
/// 在父进程活着的时候，它应当是一个完全不可见的等待者。
/// </para>
///
/// <para>
/// 恢复动作是<strong>幂等</strong>的：父进程正常退出时自己已经恢复过一次，
/// 这里再做一遍只是把同样的值再写一次，不会造成可见变化。
/// </para>
/// </remarks>
internal static class TaskbarGuardProbe
{
    /// <summary>守护进程用的命令行开关。由 <c>TaskbarHider</c> 在拉起守护时拼进命令行。</summary>
    internal const string Verb = "--taskbar-guard";

    /// <summary>参数个数非法时的退出码，与其它命令行入口的约定一致。</summary>
    private const int InvalidArgumentsExitCode = 2;

    /// <summary>
    /// 命令行里带守护开关时，在这里就跑完并退出——不进 WPF、不取单实例、不碰数据目录。
    /// </summary>
    /// <remarks>
    /// 与 <c>BackdropCaptureWorker.RunIfRequested</c> 同一套路：参数不是这个开关就返回 false，
    /// 由调用方继续走正常启动。
    /// </remarks>
    public static bool RunIfRequested(string[] arguments, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        exitCode = 0;

        if (arguments.Length != 3 || !string.Equals(arguments[0], Verb, StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(arguments[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parentProcessId)
            || parentProcessId <= 0)
        {
            exitCode = InvalidArgumentsExitCode;
            return true;
        }

        // 第三个参数是隐藏之前用户自己的自动隐藏设置，只有 "1" 算真。
        exitCode = Run(parentProcessId, arguments[2] == "1");
        return true;
    }

    /// <summary>等 <paramref name="parentProcessId"/> 结束，然后恢复任务栏。</summary>
    public static int Run(int parentProcessId, bool autoHideBefore)
    {
        if (!WaitForParentExit(parentProcessId))
        {
            // 连父进程的句柄都拿不到：无法判断它是否还活着。
            // 这时贸然恢复会与仍然活着的父进程互相打架（它会把任务栏再藏起来，
            // 而守护已经退出，就再也没人负责恢复了），所以什么都不做地退出。
            return 0;
        }

        Restore(autoHideBefore);
        return 0;
    }

    /// <summary>
    /// 等父进程结束；返回是否已经确认它结束了。
    /// </summary>
    /// <remarks>
    /// 「父进程早就没了」是正常情形之一（结束得太快），不是错误，
    /// 所以 <see cref="ArgumentException"/> 也算确认结束。
    /// </remarks>
    private static bool WaitForParentExit(int parentProcessId)
    {
        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            parent.WaitForExit();
            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>把任务栏放回来，并还原隐藏之前用户自己的自动隐藏设置。</summary>
    internal static void Restore(bool autoHideBefore)
    {
        _ = SystemTaskbar.TrySetHidden(false);
        _ = SystemTaskbar.TrySetAutoHidden(autoHideBefore);
    }
}
