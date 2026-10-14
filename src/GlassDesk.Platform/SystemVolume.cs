using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform;

/// <summary>
/// 默认播放设备的音量与静音，供系统控制区里的音量滑块使用。
/// </summary>
/// <remarks>
/// 与 <see cref="WindowControl"/> 一样包一层，让上层不必直接碰 COM。
/// 端点只在第一次读写时连接，连接失败会如实返回 <c>false</c> 并在下次调用时重试：
/// 拔掉耳机、切换默认设备都会让先前拿到的端点失效，而面板是长期开着的，
/// 不能把一次失败记成永久不可用。
///
/// <para>
/// 实例持有 COM 引用，用完必须释放，因此实现了 <see cref="IDisposable"/>。
/// </para>
/// </remarks>
public sealed class SystemVolume : IDisposable
{
    /// <summary>
    /// 一个进程内共享的包装器；调用一律带 <c>UniqueInstance</c>，它不做缓存。
    /// </summary>
    private static readonly StrategyBasedComWrappers Wrappers = ComInterop.Shared;

    private IAudioEndpointVolume? _volume;
    private bool _disposed;

    private SystemVolume()
    {
    }

    /// <summary>
    /// 创建一个音量控制句柄。此时不连接音频设备，因此不会失败。
    /// </summary>
    public static SystemVolume Open() => new();

    /// <summary>
    /// 读当前音量（0~1）与静音状态；读不到时返回 <c>false</c>。
    /// </summary>
    public bool TryGetLevel(out double level, out bool muted)
    {
        level = 0;
        muted = false;

        var volume = Acquire();

        if (volume is null
            || volume.GetMasterVolumeLevelScalar(out var scalar) < 0
            || volume.GetMute(out var isMuted) < 0)
        {
            Drop();
            return false;
        }

        level = double.IsFinite(scalar) ? Math.Clamp(scalar, 0d, 1d) : 0d;
        muted = isMuted;
        return true;
    }

    /// <summary>把主音量设到 0~1 之间的某个值。超出范围的值会被夹到区间内。</summary>
    public bool TrySetLevel(double level)
    {
        if (!double.IsFinite(level))
        {
            return false;
        }

        var volume = Acquire();
        var context = Guid.Empty;

        if (volume is null || volume.SetMasterVolumeLevelScalar((float)Math.Clamp(level, 0d, 1d), in context) < 0)
        {
            Drop();
            return false;
        }

        return true;
    }

    /// <summary>设置或取消静音。</summary>
    public bool TrySetMuted(bool muted)
    {
        var volume = Acquire();
        var context = Guid.Empty;

        if (volume is null || volume.SetMute(muted, in context) < 0)
        {
            Drop();
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Drop();
        GC.SuppressFinalize(this);
    }

    /// <summary>取（必要时建立）端点连接；已释放或不可用时返回 null。</summary>
    private IAudioEndpointVolume? Acquire()
    {
        if (_disposed)
        {
            return null;
        }

        return _volume ??= CreateEndpointVolume();
    }

    /// <summary>丢掉一个已经失效的连接，让下一次调用重新建立。</summary>
    private void Drop()
    {
        if (_volume is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _volume = null;
    }

    /// <summary>
    /// 连接默认播放设备并激活 <c>IAudioEndpointVolume</c>。
    /// </summary>
    /// <remarks>
    /// 包装器会自己 AddRef 一次，所以这里拿到的原始指针照常释放，不会多放。
    /// 任何一步失败都返回 null，不抛异常——音频设备不可用是正常状态。
    /// </remarks>
    private static IAudioEndpointVolume? CreateEndpointVolume()
    {
        nint enumeratorPointer = 0;
        nint devicePointer = 0;
        nint volumePointer = 0;
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;

        try
        {
            var hr = Ole32.CoCreateInstance(
                in CoreAudioIds.MMDeviceEnumeratorClass,
                0,
                Ole32.ClsCtxInprocServer,
                in CoreAudioIds.MMDeviceEnumerator,
                out enumeratorPointer);

            if (hr != Ole32.SoK || enumeratorPointer == 0)
            {
                return null;
            }

            enumerator = (IMMDeviceEnumerator)Wrappers.GetOrCreateObjectForComInstance(
                enumeratorPointer, CreateObjectFlags.UniqueInstance);

            if (enumerator.GetDefaultAudioEndpoint(
                    CoreAudioIds.DataFlowRender, CoreAudioIds.RoleMultimedia, out devicePointer) < 0
                || devicePointer == 0)
            {
                return null;
            }

            device = (IMMDevice)Wrappers.GetOrCreateObjectForComInstance(
                devicePointer, CreateObjectFlags.UniqueInstance);

            var interfaceId = CoreAudioIds.AudioEndpointVolume;

            if (device.Activate(in interfaceId, (int)Ole32.ClsCtxAll, 0, out volumePointer) < 0 || volumePointer == 0)
            {
                return null;
            }

            return (IAudioEndpointVolume)Wrappers.GetOrCreateObjectForComInstance(
                volumePointer, CreateObjectFlags.UniqueInstance);
        }
        catch (Exception ex) when (ComFailures.IsExpected(ex))
        {
            return null;
        }
        finally
        {
            if (volumePointer != 0)
            {
                Marshal.Release(volumePointer);
            }

            if (devicePointer != 0)
            {
                Marshal.Release(devicePointer);
            }

            if (enumeratorPointer != 0)
            {
                Marshal.Release(enumeratorPointer);
            }

            if (device is IDisposable disposableDevice)
            {
                disposableDevice.Dispose();
            }

            if (enumerator is IDisposable disposableEnumerator)
            {
                disposableEnumerator.Dispose();
            }
        }
    }
}
