using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform;

/// <summary>
/// 一个应用的音频会话，对应「音量合成器」里的一行。
/// </summary>
/// <param name="Id">会话实例标识：同一个应用多次启动会得到不同的值，用它区分具体是哪一次。</param>
/// <param name="ProcessId">所属进程。</param>
/// <param name="Name">显示名：优先程序自己声明的显示名，其次可执行文件名，最后退回进程名。</param>
/// <param name="ExecutablePath">可执行文件路径，用来取图标；取不到时为空。</param>
/// <param name="Volume">该会话的音量（0~1）。</param>
/// <param name="Muted">该会话是否静音。</param>
public sealed record AudioSession(
    string Id,
    uint ProcessId,
    string Name,
    string? ExecutablePath,
    double Volume,
    bool Muted);

/// <summary>
/// 默认播放设备上各应用的音频会话，用于「音量合成器」。
/// </summary>
/// <remarks>
/// 这是唯一能给单个应用单独调音量的途径：系统音量是「设备级」的
/// （<see cref="SystemVolume"/>），而每个应用在设备上都有自己的会话，
/// 音量与静音挂在会话上。
///
/// <para>
/// 会话由音频服务管理，随时可能新建或消失，所以写操作按
/// <see cref="AudioSession.Id"/> 重新定位一次，而不是记住会话对象：
/// 面板列出来到用户拖动之间用户可能已经关掉了那个程序。
/// </para>
/// </remarks>
public static class AudioSessions
{
    /// <summary>列出默认播放设备上此刻有声音会话的应用；按进程 id 去重。</summary>
    public static IReadOnlyList<AudioSession> List()
    {
        var sessions = new List<AudioSession>();
        var seen = new HashSet<uint>();

        var manager = SessionManager.TryOpen();

        if (manager is null)
        {
            return sessions;
        }

        try
        {
            foreach (var (id, processId, control, volume) in manager.Enumerate())
            {
                // 同一个进程可能有多个会话（多个窗口各自的媒体流），合成器里只算一行。
                if (processId == 0 || !seen.Add(processId))
                {
                    continue;
                }

                var (name, path) = Describe(processId);

                sessions.Add(new AudioSession(
                    id,
                    processId,
                    name,
                    path,
                    ReadVolume(volume),
                    ReadMute(volume)));

                _ = control;
            }
        }
        finally
        {
            manager.Dispose();
        }

        return sessions;
    }

    /// <summary>把某个会话的音量设到 0~1；找不到那个会话时返回 false。</summary>
    public static bool TrySetVolume(string sessionId, double level)
    {
        if (!double.IsFinite(level))
        {
            return false;
        }

        return TryApply(sessionId, volume =>
        {
            var context = Guid.Empty;
            return volume.SetMasterVolume((float)Math.Clamp(level, 0d, 1d), in context) >= 0;
        });
    }

    /// <summary>设置或取消某个会话的静音。</summary>
    public static bool TrySetMute(string sessionId, bool muted)
        => TryApply(sessionId, volume =>
        {
            var context = Guid.Empty;
            return volume.SetMute(muted, in context) >= 0;
        });

    private static bool TryApply(string sessionId, Func<ISimpleAudioVolume, bool> action)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return false;
        }

        var manager = SessionManager.TryOpen();

        if (manager is null)
        {
            return false;
        }

        try
        {
            foreach (var (id, _, _, volume) in manager.Enumerate())
            {
                if (string.Equals(id, sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    return action(volume);
                }
            }
        }
        finally
        {
            manager.Dispose();
        }

        return false;
    }

    private static double ReadVolume(ISimpleAudioVolume volume)
        => volume.GetMasterVolume(out var level) >= 0 && float.IsFinite(level)
            ? Math.Clamp(level, 0f, 1f)
            : 0d;

    private static bool ReadMute(ISimpleAudioVolume volume)
        => volume.GetMute(out var muted) >= 0 && muted;

    /// <summary>用进程信息给会话取一个像样的名字与图标来源。</summary>
    private static (string Name, string? Path) Describe(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            var path = TryReadPath(process);

            if (!string.IsNullOrWhiteSpace(path))
            {
                var description = TryReadDescription(path);

                if (!string.IsNullOrWhiteSpace(description))
                {
                    return (description, path);
                }

                var fileName = System.IO.Path.GetFileNameWithoutExtension(path);

                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    return (fileName, path);
                }
            }

            return (process.ProcessName, path);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return ($"进程 {processId}", null);
        }
    }

    private static string? TryReadPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? TryReadDescription(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
            or ArgumentException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static readonly StrategyBasedComWrappers Wrappers = ComInterop.Shared;

    /// <summary>
    /// 默认播放设备上的会话管理器，以及一次枚举里每个会话的三个接口。
    /// </summary>
    /// <remarks>
    /// 单独包一层是因为「打开管理器」要连着开枚举器，而每个会话又要
    /// <c>QueryInterface</c> 出两个接口；把生命周期收在一处，
    /// 调用方只需要 <c>foreach</c> 加 <c>Dispose</c>。
    /// </remarks>
    private sealed class SessionManager : IDisposable
    {
        private readonly List<nint> _sessionPointers = [];
        private nint _enumeratorPointer;
        private IMMDevice? _device;
        private IAudioSessionManager2? _manager;
        private IAudioSessionEnumerator? _enumerator;

        public static SessionManager? TryOpen()
        {
            var holder = new SessionManager();

            try
            {
                if (!holder.Open())
                {
                    holder.Dispose();
                    return null;
                }

                return holder;
            }
            catch (Exception ex) when (IsExpectedFailure(ex))
            {
                holder.Dispose();
                return null;
            }
        }

        /// <summary>逐个交出会话：实例标识、所属进程、控制接口与音量接口。</summary>
        public IEnumerable<(string Id, uint ProcessId, IAudioSessionControl2 Control, ISimpleAudioVolume Volume)> Enumerate()
        {
            if (_enumerator is null)
            {
                yield break;
            }

            if (_enumerator.GetCount(out var count) < 0)
            {
                yield break;
            }

            for (var index = 0; index < count; index++)
            {
                nint sessionPointer = 0;

                if (_enumerator.GetSession(index, out sessionPointer) < 0 || sessionPointer == 0)
                {
                    continue;
                }

                _sessionPointers.Add(sessionPointer);

                var control = Query<IAudioSessionControl2>(sessionPointer, CoreAudioIds.AudioSessionControl2);
                var volume = Query<ISimpleAudioVolume>(sessionPointer, CoreAudioIds.SimpleAudioVolume);

                if (control is null || volume is null
                    || ReadInstanceId(control) is not { Length: > 0 } id

                    // 「系统声音」会话要挡掉，而它<b>不能靠 pid 判断</b>：
                    // 这类会话的 GetProcessId 返回成功级 HRESULT 0x0889000D
                    // （AUDCLNT_S_NO_SINGLE_PROCESS）并给出创建者的 pid（实测是 taskhostw），
                    // 既不小于 0 也不等于 0，于是 pid 判据永远不触发，
                    // 合成器里就多出一行莫名其妙的「Windows 任务的主机进程」。
                    // 该用专门为此存在的接口：返回 S_OK(0) 表示「我就是一个系统声音会话」。
                    || control.IsSystemSoundsSession() == 0
                    || control.GetProcessId(out var processId) < 0)
                {
                    continue;
                }

                yield return (id, (uint)processId, control, volume);
            }
        }

        public void Dispose()
        {
            foreach (var pointer in _sessionPointers)
            {
                _ = Marshal.Release(pointer);
            }

            _sessionPointers.Clear();

            if (_enumeratorPointer != 0)
            {
                _ = Marshal.Release(_enumeratorPointer);
                _enumeratorPointer = 0;
            }

            if (_enumerator is IDisposable disposableEnumerator)
            {
                disposableEnumerator.Dispose();
            }

            if (_manager is IDisposable disposableManager)
            {
                disposableManager.Dispose();
            }

            if (_device is IDisposable disposableDevice)
            {
                disposableDevice.Dispose();
            }
        }

        private bool Open()
        {
            nint enumeratorPointer = 0;

            try
            {
                if (!AudioDeviceEnumerator.TryOpenDefaultRenderDevice(out _device, out enumeratorPointer))
                {
                    return false;
                }

                var managerId = CoreAudioIds.AudioSessionManager2;

                if (_device!.Activate(in managerId, (int)Ole32.ClsCtxAll, 0, out var managerPointer) < 0
                    || managerPointer == 0)
                {
                    return false;
                }

                try
                {
                    _manager = (IAudioSessionManager2)Wrappers.GetOrCreateObjectForComInstance(
                        managerPointer, CreateObjectFlags.UniqueInstance);
                }
                finally
                {
                    _ = Marshal.Release(managerPointer);
                }

                if (_manager.GetSessionEnumerator(out _enumeratorPointer) < 0 || _enumeratorPointer == 0)
                {
                    return false;
                }

                _enumerator = (IAudioSessionEnumerator)Wrappers.GetOrCreateObjectForComInstance(
                    _enumeratorPointer, CreateObjectFlags.UniqueInstance);

                return true;
            }
            finally
            {
                if (enumeratorPointer != 0)
                {
                    _ = Marshal.Release(enumeratorPointer);
                }
            }
        }

        private static string? ReadInstanceId(IAudioSessionControl2 control)
        {
            if (control.GetSessionInstanceIdentifier(out var pointer) < 0 || pointer == 0)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                Ole32.CoTaskMemFree(pointer);
            }
        }

        /// <summary>
        /// 显式 QueryInterface 取另一个接口。
        /// </summary>
        /// <remarks>
        /// 不靠包装器自动转换：拿到的是 <c>IAudioSessionControl</c>，而音量与进程
        /// 分别挂在另外两个接口上，显式 QI 的意图与失败点都更清楚。
        /// </remarks>
        private static T? Query<T>(nint pointer, Guid interfaceId) where T : class
        {
            if (Marshal.QueryInterface(pointer, in interfaceId, out var queried) != 0 || queried == 0)
            {
                return null;
            }

            try
            {
                return (T)Wrappers.GetOrCreateObjectForComInstance(queried, CreateObjectFlags.UniqueInstance);
            }
            finally
            {
                _ = Marshal.Release(queried);
            }
        }
    }

    private static bool IsExpectedFailure(Exception ex) => ComFailures.IsExpected(ex);
}
