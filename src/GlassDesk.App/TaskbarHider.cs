using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Shell;

namespace GlassDesk.App;

/// <summary>
/// 在 GlassDesk 运行期间隐藏系统任务栏，并保证它一定会被放回来。
/// </summary>
/// <remarks>
/// 「隐藏」是两件事叠加（见 <see cref="SystemTaskbar"/>）：打开自动隐藏让外壳把任务栏占的
/// 那一条还给窗口，再隐藏任务栏窗口让它不能被重新拉起来。
///
/// <para>
/// <b>恢复有三条路，缺一不可：</b>
/// <list type="number">
/// <item>用户关掉开关 → 当场恢复；</item>
/// <item>正常退出 → 关停路径里恢复；</item>
/// <item><b>进程被结束</b> → 进程自己已经没了，只能由<strong>守护进程</strong>代劳：
/// 它持有本进程的句柄，本进程一消失就把任务栏放回来。</item>
/// </list>
/// 另外还有一条兜底：意图存在设置里，下次启动时若任务栏还处于隐藏状态，
/// 会先恢复再按意图重新隐藏——守护进程万一也没能跑起来，任务栏不会永远回不来。
/// </para>
///
/// <para>
/// 隐藏期间每 <see cref="EnforceInterval"/> 检查一次，任务栏一旦重新出现就再隐藏。
/// 这是「运行期间不允许重新拉起」的落点：explorer 重启、或别的程序把它显示出来，
/// 都会在一秒内被收回去。
/// </para>
/// </remarks>
internal sealed class TaskbarHider : IDisposable
{
    /// <summary>
    /// 重新拉起的检查间隔。
    /// </summary>
    /// <remarks>
    /// 实测外壳自己会把藏起来的任务栏再显示出来（6000 毫秒内出现 1 次，每次约 250 毫秒），
    /// 所以这不是多余的保险，而是必须的一环。间隔取 250 毫秒：与实测的重新出现时长同量级，
    /// 万一发生也只是几乎看不见的一闪；再长就会被用户看到任务栏跳出来。
    /// 每次只是对缓存的两个句柄各做一次查询，四分之一秒一次可以忽略。
    /// </remarks>
    private static readonly TimeSpan EnforceInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>守护进程用的命令行开关，与 <c>TaskbarGuardProbe</c> 里的登记保持一致。</summary>
    internal const string GuardVerb = Startup.TaskbarGuardProbe.Verb;

    private readonly GlassDeskDataStore? _data;
    private readonly Dispatcher _dispatcher;

    private DispatcherTimer? _enforceTimer;
    private Process? _guard;
    private IReadOnlyList<nint> _taskbars = [];
    private bool _hidden;
    private bool _autoHideBefore;
    private bool _disposed;

    internal TaskbarHider(GlassDeskDataStore? data, Dispatcher dispatcher)
    {
        _data = data;
        _dispatcher = dispatcher;
    }

    /// <summary>任务栏此刻是否由本实例隐藏着。</summary>
    internal bool IsHidden => _hidden;

    /// <summary>
    /// 按持久化的意图应用一次：意图为真就隐藏，为假就恢复。
    /// </summary>
    /// <remarks>
    /// 启动时调用：上一次会话若是被直接结束进程的，任务栏可能还留着隐藏状态，
    /// 这里先恢复再按意图重新隐藏，等于自己把自己收拾干净。
    /// </remarks>
    internal bool ApplyPersistedIntent()
    {
        var settings = _data?.Settings.Current.DesktopTaskbar;
        var intent = settings?.HideTaskbar == true;

        // 恢复时要还原「改动前」的自动隐藏，所以先把上次记下的原值取回来。
        // 不取的话这里默认 false，自愈就会把用户自己开着的自动隐藏**关掉**——
        // 那不是恢复，是改了用户的 Windows 设置。
        _autoHideBefore = settings?.TaskbarAutoHideBeforeHide ?? false;

        if (intent)
        {
            return Apply(true);
        }

        // 意图是显示，但实际可能还隐藏着（上次被结束进程、守护进程也失败了）。
        if (SystemTaskbar.IsHidden() || _hidden)
        {
            Restore();
            return true;
        }

        return true;
    }

    /// <summary>隐藏或恢复；返回是否达到了目标状态。</summary>
    internal bool Apply(bool hide)
    {
        if (_disposed)
        {
            return false;
        }

        if (!hide)
        {
            Restore();
            return true;
        }

        if (_hidden)
        {
            return true;
        }

        // 先记下原值再改：恢复时要还原成改动前的样子，而不是一律关掉自动隐藏。
        _autoHideBefore = SystemTaskbar.IsAutoHidden();

        // 守护进程必须在「真的藏起来」之前就位：这两步之间如果进程被结束，
        // 先藏后拉守护就会留下一个藏着的任务栏、而且没有任何东西负责把它放回来。
        StartGuard();

        _ = SystemTaskbar.TrySetAutoHidden(true);
        var hidden = SystemTaskbar.TrySetHidden(true);

        if (!hidden)
        {
            // 一个任务栏窗口都没找到（外壳没在跑）：把自动隐藏还原回去，别留下半截改动。
            StopGuard();
            _ = SystemTaskbar.TrySetAutoHidden(_autoHideBefore);
            return false;
        }

        _hidden = true;
        _taskbars = SystemTaskbar.FindTaskbars();
        Persist(hide: true);
        StartEnforcement();
        return true;
    }

    /// <summary>把任务栏放回来，并还原改动前的自动隐藏设置。</summary>
    internal void Restore()
    {
        StopEnforcement();
        StopGuard();

        // 刻意**不看** _hidden。
        //
        // 启动自愈就是靠这里生效的：那次会话被结束进程之后任务栏还藏着，
        // 而本进程刚起来、_hidden 必然是 false——原先那句「还不是我藏的就不管」
        // 会让自愈直接返回成功，却一次都没把任务栏放回来，用户的任务栏就永远没了。
        // 下面两件事都是幂等的（把已经显示的再显示一次、把自动隐藏设回原值），多调没有代价。
        _hidden = false;
        _ = SystemTaskbar.TrySetHidden(false);
        _ = SystemTaskbar.TrySetAutoHidden(_autoHideBefore);
        Persist(hide: false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Restore();
        _enforceTimer = null;
    }

    /// <summary>
    /// 每隔一秒确认任务栏还是隐藏的。
    /// </summary>
    /// <remarks>
    /// 用轮询而不是事件钩子：任务栏被显示出来这件事没有可靠的通知来源
    /// （explorer 重启会换掉窗口句柄，事件钩子挂在旧句柄上），
    /// 而一次可见性查询的代价远低于为此维护一套钩子。
    /// </remarks>
    private void StartEnforcement()
    {
        if (_enforceTimer is not null)
        {
            _enforceTimer.Start();
            return;
        }

        _enforceTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = EnforceInterval,
        };
        _enforceTimer.Tick += OnEnforceTick;
        _enforceTimer.Start();
    }

    private void StopEnforcement() => _enforceTimer?.Stop();

    private void OnEnforceTick(object? sender, EventArgs e)
    {
        if (!_hidden || _disposed)
        {
            return;
        }

        try
        {
            // 新的任务栏窗口可能在会话中途出现（接上显示器、拓扑变化）。
            // 只看缓存的句柄会漏掉它们：那时全部缓存都还「已隐藏」，
            // 这里就直接返回了，新任务栏会一直露在外面。
            var taskbars = SystemTaskbar.FindTaskbars();

            if (taskbars.Count == _taskbars.Count && SystemTaskbar.IsHidden(_taskbars))
            {
                return;
            }

            // 被重新显示、或外壳重启换掉了句柄：重新取一遍再藏。
            _taskbars = taskbars;
            _ = SystemTaskbar.TrySetHidden(true);
        }
        catch (Exception ex)
        {
            // DispatcherTimer 的 Tick 里逃出去的异常会按未处理异常结束整个进程，
            // 而这个 Tick 每 250 毫秒就跑一次：一次意外等于四分之一秒后崩掉。
            // 隐藏任务栏只是外观功能，绝不能让它有这种杀伤力。
            _ = ex;
        }
    }

    /// <summary>
    /// 拉起守护进程：它等本进程结束，然后把任务栏放回来。
    /// </summary>
    /// <remarks>
    /// 用同一个可执行文件加一个开关，而不是另做一个 exe：便携版只有一个文件，
    /// 另做一个 exe 就得跟着一起分发、一起改路径。
    ///
    /// <para>
    /// 刻意不放进 Job 对象、也不在退出时等它：它必须能比本进程活得久，
    /// 这正是「被结束进程也能恢复」的全部意义。
    /// </para>
    /// </remarks>
    private void StartGuard()
    {
        if (_guard is not null)
        {
            return;
        }

        var executable = Environment.ProcessPath;

        if (string.IsNullOrEmpty(executable))
        {
            return;
        }

        try
        {
            _guard = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                ArgumentList =
                {
                    GuardVerb,
                    Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                    _autoHideBefore ? "1" : "0",
                },
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
            or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            // 拉不起来就不拉：正常退出这条路仍然会恢复，兜底还有下次启动时的自愈。
            _guard = null;
        }
    }

    /// <summary>本进程自己已经把任务栏放回来了，守护进程不再需要。</summary>
    private void StopGuard()
    {
        var guard = _guard;
        _guard = null;

        if (guard is null)
        {
            return;
        }

        try
        {
            if (!guard.HasExited)
            {
                guard.Kill();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 它可能刚好自己退出了。
        }
        finally
        {
            guard.Dispose();
        }
    }

    private void Persist(bool hide)
    {
        if (_data is null)
        {
            return;
        }

        try
        {
            var settings = _data.Settings.Current.DesktopTaskbar;
            settings.HideTaskbar = hide;

            if (hide)
            {
                settings.TaskbarAutoHideBeforeHide = _autoHideBefore;
            }

            _data.Settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StorageException)
        {
            // 存不下来只影响「下次启动是否重新隐藏」，不该让隐藏动作本身失败。
        }
    }
}
