using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform;

/// <summary>
/// 打开默认播放设备。
/// </summary>
/// <remarks>
/// 音量（设备级）与会话（应用级）两处都要走这一步，收在一处避免两套略有差别的打开代码。
/// </remarks>
internal static class AudioDeviceEnumerator
{
    /// <summary>
    /// 打开默认播放设备；<paramref name="enumeratorPointer"/> 由调用方释放。
    /// </summary>
    public static bool TryOpenDefaultRenderDevice(out IMMDevice? device, out nint enumeratorPointer)
    {
        device = null;
        enumeratorPointer = 0;

        try
        {
            var classId = CoreAudioIds.MMDeviceEnumeratorClass;
            var interfaceId = CoreAudioIds.MMDeviceEnumerator;

            if (Ole32.CoCreateInstance(in classId, 0, Ole32.ClsCtxInprocServer, in interfaceId, out enumeratorPointer) < 0
                || enumeratorPointer == 0)
            {
                return false;
            }

            var enumerator = (IMMDeviceEnumerator)Wrappers.GetOrCreateObjectForComInstance(
                enumeratorPointer, CreateObjectFlags.UniqueInstance);

            try
            {
                if (enumerator.GetDefaultAudioEndpoint(
                        CoreAudioIds.DataFlowRender, CoreAudioIds.RoleMultimedia, out var devicePointer) < 0
                    || devicePointer == 0)
                {
                    return false;
                }

                try
                {
                    device = (IMMDevice)Wrappers.GetOrCreateObjectForComInstance(
                        devicePointer, CreateObjectFlags.UniqueInstance);
                }
                finally
                {
                    _ = Marshal.Release(devicePointer);
                }

                return device is not null;
            }
            finally
            {
                if (enumerator is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }
        catch (Exception ex) when (ComFailures.IsExpected(ex))
        {
            return false;
        }
    }

    private static readonly StrategyBasedComWrappers Wrappers = ComInterop.Shared;
}

/// <summary>
/// 一个音频端点设备。
/// </summary>
/// <param name="Id">端点 id，切换默认设备时原样交回系统。</param>
/// <param name="Name">设备友好名，例如「扬声器 (MCHOSE V9 PRO)」。</param>
/// <param name="IsDefault">此刻是不是该方向的默认设备。</param>
public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// 系统音频输入/输出设备的枚举与默认设备切换。
/// </summary>
/// <remarks>
/// 枚举走公开的 CoreAudio（<c>IMMDeviceEnumerator</c>）；<b>切换默认设备走未公开的
/// <c>IPolicyConfig</c></b>——公开接口只能读，不能改，而设备切换类工具用的都是它。
/// 因此切换失败要如实返回 <c>false</c>（系统版本、策略、被占用都可能让它失败），
/// 由界面把控件拨回真实状态，而不是假装成功。
///
/// <para>
/// 全部调用都可能因为没有音频设备或外壳未就绪而失败，所以一律返回空表/false，不抛异常。
/// </para>
/// </remarks>
public static class AudioDevices
{
    /// <summary><c>STGM_READ</c>：只读打开属性表。</summary>
    private const int StgmRead = 0;

    /// <summary>取某个方向（播放/录音）此刻可用的设备。</summary>
    public static IReadOnlyList<AudioDevice> List(bool input)
    {
        var devices = new List<AudioDevice>();
        var flow = input ? CoreAudioIds.DataFlowCapture : CoreAudioIds.DataFlowRender;
        var defaultId = TryGetDefaultId(flow);

        nint enumeratorPointer = 0;
        nint collectionPointer = 0;
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;

        try
        {
            if (!TryCreateEnumerator(out enumeratorPointer))
            {
                return devices;
            }

            enumerator = Wrap<IMMDeviceEnumerator>(enumeratorPointer);

            if (enumerator.EnumAudioEndpoints(flow, CoreAudioIds.DeviceStateActive, out collectionPointer) < 0
                || collectionPointer == 0)
            {
                return devices;
            }

            collection = Wrap<IMMDeviceCollection>(collectionPointer);

            if (collection.GetCount(out var count) < 0 || count <= 0)
            {
                return devices;
            }

            for (var index = 0; index < count; index++)
            {
                nint devicePointer = 0;
                IMMDevice? device = null;

                try
                {
                    if (collection.Item(index, out devicePointer) < 0 || devicePointer == 0)
                    {
                        continue;
                    }

                    device = Wrap<IMMDevice>(devicePointer);
                    var id = ReadId(device);

                    if (string.IsNullOrEmpty(id))
                    {
                        continue;
                    }

                    devices.Add(new AudioDevice(
                        id,
                        ReadFriendlyName(device) ?? id,
                        string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
                }
                finally
                {
                    if (devicePointer != 0)
                    {
                        _ = Marshal.Release(devicePointer);
                    }

                    if (device is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                }
            }
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            // 没有可用音频设备、外壳未就绪、策略拒绝：都只是「这一轮列不出来」。
        }
        finally
        {
            if (collectionPointer != 0)
            {
                _ = Marshal.Release(collectionPointer);
            }

            if (enumeratorPointer != 0)
            {
                _ = Marshal.Release(enumeratorPointer);
            }

            if (collection is IDisposable disposableCollection)
            {
                disposableCollection.Dispose();
            }

            if (enumerator is IDisposable disposableEnumerator)
            {
                disposableEnumerator.Dispose();
            }
        }

        return devices;
    }

    /// <summary>
    /// 把某个端点设为该方向的默认设备。
    /// </summary>
    /// <remarks>
    /// 三个角色都设一遍（控制台、多媒体、通信）。只设多媒体的话，
    /// 语音通话类程序仍会走到旧设备上，用户会觉得「切了但没生效」。
    /// </remarks>
    public static bool TrySetDefault(string deviceId, bool input)
    {
        _ = input;

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        nint policyPointer = 0;
        IPolicyConfig? policy = null;
        var anyAccepted = false;

        try
        {
            var classId = CoreAudioIds.PolicyConfigClass;
            var interfaceId = CoreAudioIds.PolicyConfig;

            if (Ole32.CoCreateInstance(in classId, 0, Ole32.ClsCtxAll, in interfaceId, out policyPointer) < 0
                || policyPointer == 0)
            {
                return false;
            }

            policy = Wrap<IPolicyConfig>(policyPointer);

            foreach (var role in new[] { CoreAudioIds.RoleConsole, CoreAudioIds.RoleMultimedia, CoreAudioIds.RoleCommunications })
            {
                if (policy.SetDefaultEndpoint(deviceId, role) >= 0)
                {
                    anyAccepted = true;
                }
            }

            return anyAccepted;
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            return false;
        }
        finally
        {
            if (policyPointer != 0)
            {
                _ = Marshal.Release(policyPointer);
            }

            if (policy is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    /// <summary>
    /// 取某个方向此刻默认设备的端点 id；没有默认设备时返回 null。
    /// </summary>
    /// <remarks>
    /// 比 <see cref="List"/> 轻得多：只问一次默认端点，不枚举设备、不读属性表。
    /// 给「每秒确认一次默认设备有没有被别的程序改掉」这类高频路径用。
    /// </remarks>
    public static string? GetDefaultId(bool input)
        => TryGetDefaultId(input ? CoreAudioIds.DataFlowCapture : CoreAudioIds.DataFlowRender);

    /// <summary>取某个方向默认设备的端点 id；没有默认设备时返回 null。</summary>
    private static string? TryGetDefaultId(int flow)
    {
        nint enumeratorPointer = 0;
        nint devicePointer = 0;
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;

        try
        {
            if (!TryCreateEnumerator(out enumeratorPointer))
            {
                return null;
            }

            enumerator = Wrap<IMMDeviceEnumerator>(enumeratorPointer);

            return enumerator.GetDefaultAudioEndpoint(flow, CoreAudioIds.RoleMultimedia, out devicePointer) >= 0
                && devicePointer != 0
                ? ReadId(device = Wrap<IMMDevice>(devicePointer))
                : null;
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            return null;
        }
        finally
        {
            if (devicePointer != 0)
            {
                _ = Marshal.Release(devicePointer);
            }

            if (enumeratorPointer != 0)
            {
                _ = Marshal.Release(enumeratorPointer);
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

    private static bool TryCreateEnumerator(out nint pointer)
    {
        pointer = 0;

        var classId = CoreAudioIds.MMDeviceEnumeratorClass;
        var interfaceId = CoreAudioIds.MMDeviceEnumerator;

        return Ole32.CoCreateInstance(in classId, 0, Ole32.ClsCtxInprocServer, in interfaceId, out pointer) >= 0
            && pointer != 0;
    }

    private static string? ReadId(IMMDevice device)
    {
        if (device.GetId(out var pointer) < 0 || pointer == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            // 端点 id 是 CoTaskMemAlloc 分配的，必须用配对的释放函数。
            Ole32.CoTaskMemFree(pointer);
        }
    }

    /// <summary>读设备友好名；读不到时返回 null，由调用方退回端点 id。</summary>
    private static string? ReadFriendlyName(IMMDevice device)
    {
        nint storePointer = 0;
        IPropertyStore? store = null;

        try
        {
            if (device.OpenPropertyStore(StgmRead, out storePointer) < 0 || storePointer == 0)
            {
                return null;
            }

            store = Wrap<IPropertyStore>(storePointer);
            var key = CoreAudioIds.DeviceFriendlyName;

            if (store.GetValue(in key, out var value) < 0)
            {
                return null;
            }

            if (value.Type != VarType.Lpwstr || value.Pointer == 0)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(value.Pointer);
            }
            finally
            {
                Ole32.CoTaskMemFree(value.Pointer);
            }
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            return null;
        }
        finally
        {
            if (storePointer != 0)
            {
                _ = Marshal.Release(storePointer);
            }

            if (store is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private static T Wrap<T>(nint pointer) where T : class =>
        (T)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);

    private static bool IsExpectedFailure(Exception ex) => ComFailures.IsExpected(ex);

    /// <summary>一个进程内共享的包装器；调用一律带 <c>UniqueInstance</c>，它不做缓存。</summary>
    private static readonly StrategyBasedComWrappers Wrappers = ComInterop.Shared;
}
