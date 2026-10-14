using System.Diagnostics;
using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform.Shell;

/// <summary>
/// 让一个正在运行的程序退出：先请它自己关，叫不动才结束进程。
/// </summary>
/// <remarks>
/// 顺序不能颠倒。<c>Kill</c> 会跳过程序自己的保存与清理（未保存的文档、正在写的文件、
/// 托盘图标的注销），所以先对它可见的每个顶层窗口投 <c>WM_CLOSE</c>——
/// 这与用户点窗口右上角的关闭是同一件事。只有它在宽限期内没有任何反应，
/// 才退回到结束进程，并把「这是强制的」如实告诉调用方。
/// </remarks>
public static class ApplicationQuitter
{
    /// <summary>该程序此刻在运行的一个进程。</summary>
    /// <param name="ProcessId">进程 id，结束这一个进程时原样交回。</param>
    public sealed record RunningProcess(int ProcessId);

    /// <summary>
    /// 列出这个可执行文件此刻正在运行的进程。
    /// </summary>
    /// <remarks>
    /// 同一个软件多开时（两个 QQ 账号）这里会返回多条。调用方必须让用户指明是哪一个：
    /// 只凭程序路径无法区分账号，而「结束全部」会连带杀掉另一个正在使用的账号。
    /// </remarks>
    public static IReadOnlyList<RunningProcess> ListProcesses(string executablePath)
    {
        var processes = FindProcesses(executablePath);
        var result = new List<RunningProcess>(processes.Count);

        try
        {
            foreach (var process in processes)
            {
                result.Add(new RunningProcess(process.Id));
            }
        }
        finally
        {
            DisposeAll(processes);
        }

        return result;
    }

    /// <summary>
    /// 结束<b>指定进程</b>：先请它自己关，叫不动才结束它。
    /// </summary>
    /// <remarks>
    /// 多账号软件必须走到这里，而不是按程序路径批量结束——那样会把同一个软件的
    /// 其他账号一起杀掉。实测就是「结束一个 QQ 账号，结果两个都没了」。
    /// </remarks>
    public static bool TryQuitProcess(int processId, TimeSpan gracefulTimeout)
    {
        Process process;

        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            // 它已经退出了：目标达成。
            return true;
        }

        using (process)
        {
            return Quit(new List<Process> { process }, gracefulTimeout);
        }
    }

    /// <summary>
    /// 结束这个可执行文件<b>对应的全部进程</b>。
    /// </summary>
    /// <remarks>
    /// <b>只允许由用户明确的「全部退出」选择调用。</b>同一个软件多开时
    /// （两个 QQ 账号就是同一个 exe 的两个进程）这个方法会把每个账号都关掉——
    /// 这正是它存在的意义，也是它必须在方法名和菜单标签上都写清楚的原因：
    /// 用户点的是「全部退出」，他就应该得到全部退出，而不是被谁替他挑一个。
    ///
    /// <para>
    /// 按程序路径去猜「他点的是哪一个」是做不到的（路径相同，图标与进程之间
    /// 没有任何可查的对应关系），所以那个歧义不在这里解决——这里做的事是明确的。
    /// </para>
    /// </remarks>
    /// <param name="executablePath">目标程序的可执行文件路径。</param>
    /// <param name="gracefulTimeout">等它们自己退出的宽限期。</param>
    /// <returns>这些进程是否都不再运行。</returns>
    public static bool TryQuitAll(string executablePath, TimeSpan gracefulTimeout)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        var processes = FindProcesses(executablePath);

        if (processes.Count == 0)
        {
            // 没有正在运行的进程：它已经退出了，对调用方来说目标已达成。
            return true;
        }

        return Quit(processes, gracefulTimeout);
    }

    /// <summary>先请这些进程自己关，宽限期内不理会才结束它们。</summary>
    private static bool Quit(List<Process> processes, TimeSpan gracefulTimeout)
    {
        try
        {
            // 1. 先请它自己关。
            //    这里**只**动这个进程自己的主窗口，不去枚举「该程序的所有窗口」：
            //    同一个 exe 多开时（两个 QQ 账号），按程序路径找出来的窗口属于两个账号，
            //    一旦对它们投 WM_CLOSE，用户点一个账号就会把另一个账号也关掉。
            //    宁可少关一个窗口，也不能替用户关掉他没点的那个账号。
            foreach (var process in processes)
            {
                TryCloseMainWindow(process);
            }

            if (WaitForExit(processes, gracefulTimeout))
            {
                return true;
            }

            // 2. 宽限期内没反应，才结束进程。
            foreach (var process in processes)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException
                    or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // 它刚好自己退了，或者权限不够：如实反映在返回值里。
                }
            }

            return WaitForExit(processes, TimeSpan.FromSeconds(2));
        }
        finally
        {
            // 句柄必须留到强制结束之后才能释放：提前释放会让 Kill 那一轮拿到空对象，
            // 实测就是「杀了但没杀掉，还返回失败」。
            // 无条件释放，不做「由调用方负责」的约定——那种约定漏一条分支就是句柄泄漏。
            DisposeAll(processes);
        }
    }

    /// <summary>这个程序此刻有没有在运行。</summary>
    public static bool IsRunning(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        var processes = FindProcesses(executablePath);

        try
        {
            return processes.Count > 0;
        }
        finally
        {
            DisposeAll(processes);
        }
    }

    /// <summary>按可执行文件路径找出正在运行的进程。</summary>
    /// <remarks>
    /// 先用进程名缩小范围，再去核对完整路径：逐进程读映像路径在本机要 1.6 秒左右，
    /// 而这一步只是为了让「退出」点下去立刻有反应。
    /// <b>名字相同绝不当作命中</b>——这里的结果会用来结束进程，
    /// 必须确认是同一个可执行文件，宁可少杀也不能杀错。
    /// </remarks>
    private static List<Process> FindProcesses(string executablePath)
    {
        var matches = new List<Process>();
        var name = System.IO.Path.GetFileNameWithoutExtension(executablePath);

        if (name.Length == 0)
        {
            return matches;
        }

        Process[] candidates;

        try
        {
            candidates = Process.GetProcessesByName(name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return matches;
        }

        foreach (var process in candidates)
        {
            var keep = false;

            try
            {
                var path = process.MainModule?.FileName;

                if (!string.IsNullOrWhiteSpace(path)
                    && string.Equals(path, executablePath, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(process);
                    keep = true;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                or InvalidOperationException or NotSupportedException)
            {
                // 读不到映像：无法确认它就是我们找的那个，不结束它。
            }
            finally
            {
                if (!keep)
                {
                    process.Dispose();
                }
            }
        }

        return matches;
    }

    private static void TryCloseMainWindow(Process process)
    {
        try
        {
            if (!process.HasExited && process.MainWindowHandle != 0)
            {
                _ = process.CloseMainWindow();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    /// <summary>等这些进程全部退出；<b>不释放句柄</b>，由调用方在最后统一释放。</summary>
    private static bool WaitForExit(List<Process> processes, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (AllExited(processes))
            {
                return true;
            }

            Thread.Sleep(120);
        }

        return AllExited(processes);
    }

    private static bool AllExited(List<Process> processes)
    {
        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                // 拿不到状态就不算已退出，交给外层继续等或强制结束。
                return false;
            }
        }

        return true;
    }

    private static void DisposeAll(List<Process> processes)
    {
        foreach (var process in processes)
        {
            process.Dispose();
        }
    }
}
